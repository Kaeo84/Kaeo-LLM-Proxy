using System.Data.Common;

namespace Kaeo.LlmProxy.Modules;

/// <summary>
/// Gateway to the shared application database. All members execute against the single database
/// file the host owns and are serialized with the host's own data access. Only ADO.NET base
/// types are used so modules never take a dependency on a specific database provider.
/// Parameter names use the <c>$name</c> convention (e.g. <c>$enabled</c>); create parameters
/// with <see cref="DbCommand.CreateParameter"/> inside the configure callbacks.
/// </summary>
public interface IModuleDatabase
{
    /// <summary>
    /// Executes a module's baseline schema script (DDL) against the application database.
    /// The script must be idempotent (CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS).
    /// </summary>
    void ExecuteSchemaScript(string script);

    /// <summary>Executes a non-query command and returns the number of rows affected.</summary>
    int Execute(string commandText, Action<DbCommand>? configureCommand = null);

    /// <summary>Executes a command and returns the first column of the first row, or null.</summary>
    object? ExecuteScalar(string commandText, Action<DbCommand>? configureCommand = null);

    /// <summary>Executes a query and maps every row with <paramref name="map"/>.</summary>
    IReadOnlyList<T> Query<T>(
        string commandText,
        Func<DbDataReader, T> map,
        Action<DbCommand>? configureCommand = null);
}
