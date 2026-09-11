// CA-A-IA — Tests de segmentación del historial en conversaciones.

using CaAIA.Application.Services;
using CaAIA.Domain.Persistence;

namespace CaAIA.Tests.Unit;

public sealed class ConversationHistoryTests
{
    private static StoredChatMessage Msg(long id, string role, string text) =>
        new(id, null, role, text, new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero).AddMinutes(id));

    private static StoredChatMessage Divider(long id) => Msg(id, "system", ConversationHistory.DividerText);

    [Fact]
    public void Segment_SplitsByDivider_MostRecentFirst()
    {
        var messages = new[]
        {
            Msg(1, "user", "primera petición"),
            Msg(2, "agent", "respuesta uno"),
            Divider(3),
            Msg(4, "user", "segunda petición"),
            Msg(5, "agent", "respuesta dos"),
        };

        var segments = ConversationHistory.Segment(messages);
        Assert.Equal(2, segments.Count);
        Assert.Equal("segunda petición", segments[0].Title);
        Assert.Equal(2, segments[0].MessageCount);
        Assert.Equal("respuesta dos", segments[0].Preview);
        Assert.Equal("primera petición", segments[1].Title);
    }

    [Fact]
    public void Segment_SkipsDividerOnlyBlocks_AndUsesFallbackTitle()
    {
        var messages = new[]
        {
            Divider(1),
            Divider(2),
            Msg(3, "agent", "nota sin pregunta"),
        };

        var segments = ConversationHistory.Segment(messages);
        Assert.Single(segments);
        Assert.Equal("nota sin pregunta", segments[0].Title);
        Assert.Equal(2, segments[0].Messages.Count); // divisor como cabecera + nota
    }

    [Fact]
    public void Segment_Empty_ReturnsEmpty()
    {
        Assert.Empty(ConversationHistory.Segment(Array.Empty<StoredChatMessage>()));
    }

    [Fact]
    public void Segment_TrimsLongTitles()
    {
        var messages = new[] { Msg(1, "user", new string('x', 200)) };
        var segments = ConversationHistory.Segment(messages);
        Assert.Single(segments);
        Assert.Equal(48, segments[0].Title.Length);
    }
}
