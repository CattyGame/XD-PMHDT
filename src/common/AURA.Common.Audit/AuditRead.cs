namespace AURA.Common.Audit;

public static class AuditRead
{
    public static async Task RequireAsync(IAuditWriter writer, AuditEvent e, Stream content, CancellationToken ct)
    {
        try { await writer.AppendAsync(e, ct); }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }
}
