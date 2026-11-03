namespace Zr3i.Data;

/// <summary>A migration was started earlier and never finished. A person checks the database before it runs again.</summary>
public sealed class MigrationInterruptedException(string migrationId)
    : InvalidOperationException($"Migration '{migrationId}' was started but did not finish. Check the database, then clear its record in _migrations.");
