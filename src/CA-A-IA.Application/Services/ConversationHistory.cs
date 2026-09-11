// CA-A-IA — Segmentación del historial en conversaciones (puro y testeable).
// Una conversación = bloque de mensajes entre separadores "Nueva conversación".

using CaAIA.Domain.Persistence;

namespace CaAIA.Application.Services;

/// <summary>Una conversación del historial (título, fechas y mensajes en orden).</summary>
public sealed record ConversationSegment(
    long FirstId,
    string Title,
    DateTimeOffset StartedAt,
    int MessageCount,
    string Preview,
    IReadOnlyList<StoredChatMessage> Messages)
{
    public string Meta => $"{StartedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {MessageCount} mensajes";
}

/// <summary>
/// Agrupa mensajes persistidos en conversaciones por el separador del Chat.
/// La más reciente primero; se omiten bloques vacíos (solo separador).
/// </summary>
public static class ConversationHistory
{
    public const string DividerText = "—— Nueva conversación ——";

    public static bool IsDivider(StoredChatMessage message) =>
        message.Role == "system" && message.Text.Trim() == DividerText;

    public static IReadOnlyList<ConversationSegment> Segment(IReadOnlyList<StoredChatMessage> messages)
    {
        var segments = new List<List<StoredChatMessage>> { new() };
        foreach (var message in messages)
        {
            if (IsDivider(message) && segments[^1].Count > 0)
            {
                segments.Add(new List<StoredChatMessage>());
            }

            segments[^1].Add(message);
        }

        var result = new List<ConversationSegment>();
        foreach (var items in segments)
        {
            var content = items.Where(m => !IsDivider(m)).ToList();
            if (content.Count == 0)
            {
                continue;
            }

            var firstUser = content.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Text));
            var title = firstUser is not null
                ? Trim(firstUser.Text, 48)
                : Trim(content[0].Text, 48);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Conversación {content[0].At.ToLocalTime():dd/MM HH:mm}";
            }

            result.Add(new ConversationSegment(
                content[0].Id,
                title,
                content[0].At,
                content.Count,
                Trim(content[^1].Text, 90),
                items));
        }

        result.Reverse();
        return result;
    }

    private static string Trim(string value, int max)
    {
        var text = (value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }
}
