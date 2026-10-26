using Azure.Data.Tables;

namespace PoLocalCompare.Api.Common.Persistence;

/// <summary>
/// Creates every table the app uses, in every environment, before the first request. Fail-fast
/// (standards §5.6): unreachable storage stops startup rather than surfacing as request-time 500s.
/// The short retry covers Azurite still starting when the API launches.
/// </summary>
public static class StorageTables
{
    private static readonly string[] All = ["Models", "Duels", "DuelResults", "EloHistory", "Tournaments", SpendLedger.TableName];

    public static async Task EnsureAllAsync(TableServiceClient tables)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var name in All)
                    await tables.CreateTableIfNotExistsAsync(name);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
