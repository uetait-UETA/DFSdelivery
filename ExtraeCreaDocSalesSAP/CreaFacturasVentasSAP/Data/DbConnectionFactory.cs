using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace CreaFacturasVentasSAP.Data;

public interface IDbConnectionFactory
{
    SqlConnection CreateInternal();
    SqlConnection CreateExternal();
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _internal;
    private readonly string _external;

    public DbConnectionFactory(IConfiguration cfg)
    {
        _internal = cfg.GetConnectionString("InternalDb")
            ?? throw new InvalidOperationException("ConnectionString 'InternalDb' no configurado.");
        _external = cfg.GetConnectionString("ExternalDb")
            ?? throw new InvalidOperationException("ConnectionString 'ExternalDb' no configurado.");
    }

    public SqlConnection CreateInternal() => new(_internal);
    public SqlConnection CreateExternal() => new(_external);
}
