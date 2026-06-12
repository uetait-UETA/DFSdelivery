using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace ExtraeCreaDocSalesSAP.Data;

public interface IDbConnectionFactory
{
    SqlConnection CreateExternal();
    SqlConnection CreateInternal();
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _externalConnStr;
    private readonly string _internalConnStr;

    public DbConnectionFactory(IConfiguration configuration)
    {
        _externalConnStr = configuration.GetConnectionString("ExternalDb")
            ?? throw new InvalidOperationException("Falta ConnectionString 'ExternalDb' en appsettings.");
        _internalConnStr = configuration.GetConnectionString("InternalDb")
            ?? throw new InvalidOperationException("Falta ConnectionString 'InternalDb' en appsettings.");
    }

    public SqlConnection CreateExternal() => new(_externalConnStr);
    public SqlConnection CreateInternal() => new(_internalConnStr);
}
