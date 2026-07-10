using System.Collections.Generic;
using Apache.Arrow.Ipc;

namespace AdbcDrivers.Snowflake.Native.Services.Query;

/// <summary>
/// Represents the result of a query execution.
/// </summary>
internal class QueryResult
{
    /// <summary>
    /// Gets or sets the query execution status.
    /// </summary>
    public QueryStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the Arrow array stream containing the results.
    /// </summary>
    public IArrowArrayStream? ResultStream { get; set; }

    /// <summary>
    /// Gets or sets the number of rows affected or returned.
    /// </summary>
    public long RowCount { get; set; }

    /// <summary>
    /// Gets or sets the affected-row count parsed from a DML statement's row-count summary,
    /// or null when the statement was not DML. Kept separate from <see cref="RowCount"/> and
    /// <see cref="ResultStream"/> so a DML result can carry both its summary result set (for
    /// ExecuteQuery) and the count (for ExecuteUpdate).
    /// </summary>
    public long? AffectedRows { get; set; }

    /// <summary>
    /// Gets or sets any errors that occurred during execution.
    /// </summary>
    public List<QueryError> Errors { get; set; } = [];
}