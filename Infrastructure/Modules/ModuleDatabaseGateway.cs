using System.Data.Common;
using Kaeo.LlmProxy.Modules;

namespace Kaeo.LlmProxy.Infrastructure.Modules;

/// <summary>
/// Implements the contracts-library <see cref="IModuleDatabase"/> gateway over the shared
/// <see cref="AppDatabase"/> so module data lives in the existing database file and every
/// command is serialized with the host's own data access.
/// </summary>
internal sealed class ModuleDatabaseGateway(AppDatabase database) : IModuleDatabase
{
    private readonly AppDatabase _database = database;

    public void ExecuteSchemaScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        _database.ExecuteModuleSchemaScript(script);
    }

    public int Execute(string commandText, Action<DbCommand>? configureCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        return _database.ExecuteModuleNonQuery(commandText, configureCommand);
    }

    public object? ExecuteScalar(string commandText, Action<DbCommand>? configureCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        return _database.ExecuteModuleScalar(commandText, configureCommand);
    }

    public IReadOnlyList<T> Query<T>(
        string commandText,
        Func<DbDataReader, T> map,
        Action<DbCommand>? configureCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        ArgumentNullException.ThrowIfNull(map);
        return _database.ExecuteModuleQuery(commandText, map, configureCommand);
    }
}
