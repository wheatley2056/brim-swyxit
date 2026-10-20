using Brim.SwyxIt;

namespace Brim.SwyxIt.Tests;

public class CallTrackerTests
{
    private static LineSnapshot Line(int index, LineState state, string name = "Anna Berg", string number = "+49 30 1234", bool outgoing = false) =>
        new(index, state, name, number, "Vertrieb", outgoing, null);

    [Fact]
    public void Ringing_and_knocking_are_incoming_outgoing_ringing_is_an_own_call()
    {
        var tracker = new CallTracker();

        var changes = tracker.Update([
            Line(0, LineState.Active),
            Line(1, LineState.Knocking),
            Line(2, LineState.Ringing, outgoing: true),
            Line(3, LineState.Inactive),
            Line(4, LineState.HookOffInternal),
        ]);

        Assert.Equal([1], changes.Incoming.Select(c => c.Line));
        // Ausgehend klingeln und Hörer abgehoben sind eigene Anrufe (Widget zeigt „Klingelt …“ bzw. „Wählen …“).
        Assert.Equal([0, 2, 4], changes.Calls.Select(c => c.Line));
    }

    [Theory]
    [InlineData((int)LineState.HookOffExternal, false, "Wählen …")]
    [InlineData((int)LineState.Dialing, true, "Wählt …")]
    [InlineData((int)LineState.Alerting, true, "Klingelt …")]
    [InlineData((int)LineState.Ringing, true, "Klingelt …")]
    [InlineData((int)LineState.Busy, true, "Besetzt")]
    [InlineData((int)LineState.OnHold, false, "Gehalten")]
    public void Every_phase_of_a_call_has_a_status(int state, bool outgoing, string expected)
    {
        Assert.Equal(expected, CallTracker.Status(Line(0, (LineState)state, outgoing: outgoing), DateTime.Now));
    }

    [Fact]
    public void Talking_shows_the_duration_and_names_read_naturally()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0);
        var call = new LineSnapshot(0, LineState.Active, "Anna", "1", "", false, now.AddSeconds(-187));
        Assert.Equal("3:07", CallTracker.Status(call, now));
        Assert.Equal("Tobias Kern", CallTracker.PersonName("Kern , Tobias"));
        Assert.Equal("Anna Berg", CallTracker.PersonName("Anna Berg"));
    }

    [Fact]
    public void Lines_that_go_idle_are_reported_once()
    {
        var tracker = new CallTracker();
        tracker.Update([Line(0, LineState.Ringing)]);

        var answered = tracker.Update([Line(0, LineState.Active)]);
        Assert.Empty(answered.Gone);
        Assert.Single(answered.Calls);

        var ended = tracker.Update([Line(0, LineState.Inactive)]);
        Assert.Equal([0], ended.Gone);
        Assert.Empty(tracker.Update([Line(0, LineState.Inactive)]).Gone);
    }

    [Theory]
    [InlineData("Anna Berg", "+49 30 1234", "Anna Berg")]
    [InlineData(" ", "+49 30 1234", "+49 30 1234")]
    [InlineData("", "", "Unbekannt")]
    public void Caller_falls_back_to_number(string name, string number, string expected)
    {
        Assert.Equal(expected, Line(0, LineState.Ringing, name, number).Caller);
    }

    [Fact]
    public void Duration_format()
    {
        Assert.Equal("3:07", CallTracker.Duration(TimeSpan.FromSeconds(187)));
        Assert.Equal("1:02:03", CallTracker.Duration(TimeSpan.FromSeconds(3723)));
        Assert.Equal("0:00", CallTracker.Duration(TimeSpan.FromSeconds(-4)));
    }
}

public class QuickMenuTests
{
    [Fact]
    public void Keypad_collects_digits_with_limits()
    {
        var digits = "";
        foreach (var key in new[] { "+", "4", "9", "x", "+", "3", "0" })
        {
            digits = Keypad.Append(digits, key);
        }
        Assert.Equal("+4930", digits);
        Assert.Equal("+493", Keypad.Backspace(digits));
        Assert.Equal("", Keypad.Backspace(""));
        Assert.Equal(32, Enumerable.Range(0, 40).Aggregate("", (d, _) => Keypad.Append(d, "1")).Length);
    }

    [Theory]
    // Zustände als Zahl, weil LineState und TransferStep intern sind (wie CLMgrLineStates: 4 wählt, 5 klingelt, 7 besetzt, 8 aktiv).
    [InlineData(4, 1, "Wait")]
    [InlineData(5, 1, "Complete")]
    [InlineData(8, 1, "Complete")]
    [InlineData(7, 1, "Cancel")]
    [InlineData(0, 1, "Wait")]
    [InlineData(4, 25, "Cancel")]
    public void Transfer_completes_when_the_target_rings(int target, int secondsLater, string expected)
    {
        var started = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        var transfer = new PendingTransfer(0, 1, "220", started);

        Assert.Equal(expected, transfer.Next((LineState)target, started.AddSeconds(secondsLater)).ToString());
    }

    [Fact]
    public void Glow_rises_at_once_falls_slowly_and_ignores_noise()
    {
        Assert.Equal(0, CallTracker.Glow(0, 0.004));
        var loud = CallTracker.Glow(0, 0.25);
        Assert.Equal(0.567, loud, 3);
        Assert.Equal(1, CallTracker.Glow(loud, 4));

        // Stille: klingt in einigen Schritten ab statt sofort zu verschwinden.
        var falling = CallTracker.Glow(loud, 0);
        Assert.Equal(loud * 0.72, falling, 3);
        Assert.True(CallTracker.Glow(falling, 0) < falling);
    }
}
