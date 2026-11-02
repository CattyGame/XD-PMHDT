using System.Globalization;
using StackExchange.Redis;

namespace AURA.Common.Cache;

public sealed record BatchProgressSnapshot(Guid BatchId, int Total, int Processing, int Completed, int Failed,
    string Status, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record BatchImageCompletionResult(Guid BatchId, int Total, int Completed, int Failed, int Processing,
    bool IsBatchFinished, string BatchStatus);
public sealed record BatchImageOutcome(Guid ImageId, bool IsSuccess);

public interface IBatchRedisProgressService
{
    Task InitializeBatchAsync(Guid batchId, int totalImages, TimeSpan? expiry = null);
    Task<BatchProgressSnapshot?> GetProgressAsync(Guid batchId);
    Task<BatchImageCompletionResult> RecordImageOutcomeAsync(Guid batchId, Guid imageId, bool isSuccess);
    Task RebuildBatchAsync(Guid batchId, int totalImages, IReadOnlyList<BatchImageOutcome> outcomes,
        string status, TimeSpan? expiry = null);
}

public sealed class BatchRedisProgressService : IBatchRedisProgressService
{
    private readonly IDatabase _db;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private const string InitializeScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        redis.call('HSET', KEYS[1], 'batchId', ARGV[1], 'total', ARGV[2], 'processing', ARGV[2],
            'completed', 0, 'failed', 0, 'status', 'IN_PROGRESS', 'createdAt', ARGV[3], 'updatedAt', ARGV[3])
        redis.call('EXPIRE', KEYS[1], ARGV[4])
        redis.call('EXPIRE', KEYS[2], ARGV[4])
        return 1
        """;

    private const string RecordOutcomeScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return {-1, 0, 0, 0, 'MISSING', 0} end
        local total = tonumber(redis.call('HGET', KEYS[1], 'total') or '0')
        if total < 1 then return {-1, 0, 0, 0, 'INVALID', 0} end
        local completed = tonumber(redis.call('HGET', KEYS[1], 'completed') or '0')
        local failed = tonumber(redis.call('HGET', KEYS[1], 'failed') or '0')
        local processing = tonumber(redis.call('HGET', KEYS[1], 'processing') or '0')
        local status = redis.call('HGET', KEYS[1], 'status') or 'IN_PROGRESS'
        if redis.call('SADD', KEYS[2], ARGV[1]) == 0 then
            return {completed, failed, processing, total, status, status == 'IN_PROGRESS' and 0 or 1}
        end
        if processing <= 0 or completed + failed >= total then
            redis.call('SREM', KEYS[2], ARGV[1])
            return {completed, failed, processing, total, status, 0}
        end
        if ARGV[2] == '1' then completed = completed + 1 else failed = failed + 1 end
        processing = processing - 1
        if processing == 0 then
            if failed == 0 then status = 'COMPLETED'
            elseif completed == 0 then status = 'FAILED'
            else status = 'PARTIAL_COMPLETED' end
        end
        redis.call('HSET', KEYS[1], 'completed', completed, 'failed', failed, 'processing', processing,
            'status', status, 'updatedAt', ARGV[3])
        return {completed, failed, processing, total, status, 1}
        """;

    private const string RebuildScript = """
        redis.call('DEL', KEYS[1], KEYS[2])
        local total = tonumber(ARGV[2])
        local completed = 0
        local failed = 0
        for i = 8, #ARGV, 2 do
            redis.call('SADD', KEYS[2], ARGV[i])
            if ARGV[i + 1] == '1' then completed = completed + 1 else failed = failed + 1 end
        end
        local processing = total - completed - failed
        redis.call('HSET', KEYS[1], 'batchId', ARGV[1], 'total', total, 'processing', processing,
            'completed', completed, 'failed', failed, 'status', ARGV[5], 'createdAt', ARGV[6], 'updatedAt', ARGV[6])
        redis.call('EXPIRE', KEYS[1], ARGV[7])
        redis.call('EXPIRE', KEYS[2], ARGV[7])
        return {completed, failed, processing}
        """;

    public BatchRedisProgressService(IConnectionMultiplexer redis) =>
        _db = (redis ?? throw new ArgumentNullException(nameof(redis))).GetDatabase();

    public static string GetKey(Guid batchId) => $"batch:{batchId:D}:progress";

    private static string CompletedImagesKey(Guid batchId) => $"batch:{batchId:D}:completed-images";

    public async Task InitializeBatchAsync(Guid batchId, int totalImages, TimeSpan? expiry = null)
    {
        if (batchId == Guid.Empty || totalImages is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(totalImages), "Batch must contain between 1 and 100 images.");
        var ttl = expiry ?? DefaultTtl;
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiry));
        var seconds = Math.Max(1, (long)Math.Ceiling(ttl.TotalSeconds));
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        await _db.ScriptEvaluateAsync(InitializeScript,
            [GetKey(batchId), CompletedImagesKey(batchId)],
            [batchId.ToString("D"), totalImages, now, seconds]);
    }

    public async Task<BatchProgressSnapshot?> GetProgressAsync(Guid batchId)
    {
        if (batchId == Guid.Empty) return null;
        var entries = await _db.HashGetAllAsync(GetKey(batchId));
        if (entries.Length == 0) return null;
        var dict = entries.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString(), StringComparer.Ordinal);
        if (!TryInt(dict, "total", out var total) || total is < 1 or > 100
            || !TryInt(dict, "processing", out var processing) || !TryInt(dict, "completed", out var completed)
            || !TryInt(dict, "failed", out var failed) || completed + failed + processing != total
            || !dict.TryGetValue("status", out var status)
            || !DateTime.TryParse(dict.GetValueOrDefault("createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt)
            || !DateTime.TryParse(dict.GetValueOrDefault("updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt))
            throw new InvalidOperationException("Redis batch progress data is invalid.");
        return new(batchId, total, processing, completed, failed, status, createdAt, updatedAt);
    }

    public async Task<BatchImageCompletionResult> RecordImageOutcomeAsync(Guid batchId, Guid imageId, bool isSuccess)
    {
        if (batchId == Guid.Empty || imageId == Guid.Empty) throw new ArgumentException("Batch and image IDs are required.");
        var raw = (RedisResult[]?)await _db.ScriptEvaluateAsync(RecordOutcomeScript,
            [GetKey(batchId), CompletedImagesKey(batchId)],
            [imageId.ToString("D"), isSuccess ? 1 : 0, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)]);
        if (raw is null || raw.Length != 6) throw new InvalidOperationException("Redis returned an invalid batch counter result.");
        var total = (int)raw[3];
        if (total < 1) throw new InvalidOperationException("Batch progress was not initialized in Redis.");
        var completed = (int)raw[0];
        var failed = (int)raw[1];
        var processing = (int)raw[2];
        var status = (string?)raw[4] ?? "IN_PROGRESS";
        return new(batchId, total, completed, failed, processing, (long)raw[5] == 1 && processing == 0, status);
    }

    public async Task RebuildBatchAsync(Guid batchId, int totalImages, IReadOnlyList<BatchImageOutcome> outcomes,
        string status, TimeSpan? expiry = null)
    {
        if (batchId == Guid.Empty || totalImages is < 1 or > 100 || outcomes is null
            || outcomes.Count > totalImages || outcomes.Any(x => x.ImageId == Guid.Empty)
            || outcomes.Select(x => x.ImageId).Distinct().Count() != outcomes.Count
            || status is not ("IN_PROGRESS" or "COMPLETED" or "PARTIAL_COMPLETED" or "FAILED"))
            throw new ArgumentOutOfRangeException(nameof(totalImages), "Invalid durable batch progress snapshot.");
        var ttl = expiry ?? DefaultTtl;
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiry));
        var seconds = Math.Max(1, (long)Math.Ceiling(ttl.TotalSeconds));
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var values = new List<RedisValue>
        {
            batchId.ToString("D"), totalImages, outcomes.Count(x => x.IsSuccess), outcomes.Count(x => !x.IsSuccess),
            status, now, seconds
        };
        foreach (var outcome in outcomes)
        {
            values.Add(outcome.ImageId.ToString("D"));
            values.Add(outcome.IsSuccess ? 1 : 0);
        }
        await _db.ScriptEvaluateAsync(RebuildScript,
            [GetKey(batchId), CompletedImagesKey(batchId)], values.ToArray());
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> values, string key, out int result) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result)
        || (result = 0) == 1;
}
