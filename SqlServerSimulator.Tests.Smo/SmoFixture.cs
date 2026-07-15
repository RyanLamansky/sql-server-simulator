using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;

namespace SqlServerSimulator;

/// <summary>
/// The one shared fixture the SMO oracle drives: a <see cref="Simulation"/>
/// seeded with a compact WWI-shaped schema, a <c>smo</c> login, and a TDS
/// listener on an OS-assigned port. Real <c>Microsoft.SqlServer.Management.Smo</c>
/// (the library behind SSMS Object Explorer + Script-As) connects over loopback
/// against this, exactly as SSMS would.
///
/// The schema doubles as documentation of what the SMO oracle exercises: two
/// schemas (<c>Sales</c> / <c>Application</c>); an identity clustered PK; an FK
/// web spanning both schemas plus a multi-column FK; nonclustered indexes (one
/// with <c>INCLUDE</c>, one filtered); named + auto-named DEFAULT constraints;
/// a CHECK constraint; a computed column; a <c>rowversion</c> column; a
/// system-versioned temporal pair; extended properties on a table and a column;
/// an AFTER INSERT trigger; a view, a stored procedure, and a sequence; and
/// seed rows so <c>sys.partitions.rows</c> is non-zero.
/// </summary>
internal static class SmoFixture
{
    private static SimulatedNetworkListener? listener;

    /// <summary>The database every SMO test targets — the simulation's default user database.</summary>
    public const string DatabaseName = "simulated";

    /// <summary>Connection string SMO connects with (loopback TDS + TLS, the <c>smo</c> login).</summary>
    public static string ConnectionString { get; private set; } = "";

    public static void Initialize()
    {
        var sim = new Simulation();
        SeedSchema(sim);
        // The listener roots the simulation for the run; no separate field needed.
        listener = sim.ListenAsync(0).GetAwaiter().GetResult();
        ConnectionString =
            $"Server=127.0.0.1,{listener.Port};Database={DatabaseName};User ID=smo;Password=smo;" +
            "TrustServerCertificate=True;Encrypt=True;Connect Timeout=30";
    }

    public static void Cleanup()
    {
        listener?.Dispose();
        listener = null;
    }

    /// <summary>
    /// Builds a fresh SMO <see cref="Server"/> over its own loopback connection.
    /// SMO caches metadata per <see cref="Server"/>, so each test takes its own —
    /// the queries run against the in-process simulator in milliseconds.
    /// </summary>
    public static Server NewServer() =>
        new(new ServerConnection(new SqlConnection(ConnectionString)));

    private static void SeedSchema(Simulation sim)
    {
        // Batches run one at a time: CREATE VIEW / PROCEDURE / TRIGGER must each
        // be the sole statement in their batch (SQL Server's grammar rule), and
        // the in-process command doesn't split on GO.
        string[] batches =
        [
            "CREATE SCHEMA Sales",
            "CREATE SCHEMA Application",
            """
            CREATE TABLE Application.People (
                PersonID    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_People PRIMARY KEY CLUSTERED,
                FullName    nvarchar(100) NOT NULL,
                IsEmployee  bit NOT NULL CONSTRAINT DF_People_IsEmployee DEFAULT (0))
            """,
            """
            CREATE TABLE Sales.Customers (
                CustomerID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY CLUSTERED,
                CustomerName            nvarchar(100) NOT NULL,
                PrimaryContactPersonID  int NOT NULL CONSTRAINT FK_Customers_People REFERENCES Application.People (PersonID),
                CreditLimit             decimal(18, 2) NULL CONSTRAINT CK_Customers_CreditLimit CHECK (CreditLimit >= 0),
                DiscountPercent         decimal(5, 2) NOT NULL CONSTRAINT DF_Customers_Discount DEFAULT (0),
                IsOnCreditHold          bit NOT NULL DEFAULT (0),
                CreditLimitWithTax      AS (CreditLimit * 1.1),
                ConcurrencyToken        rowversion)
            """,
            """
            CREATE TABLE Sales.Orders (
                OrderID         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED,
                CustomerID      int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES Sales.Customers (CustomerID),
                OrderReference  nvarchar(20) NOT NULL,
                CONSTRAINT UQ_Orders UNIQUE (OrderID, CustomerID))
            """,
            """
            CREATE TABLE Sales.OrderLines (
                OrderLineID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderLines PRIMARY KEY CLUSTERED,
                OrderID     int NOT NULL,
                CustomerID  int NOT NULL,
                CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderID, CustomerID)
                    REFERENCES Sales.Orders (OrderID, CustomerID))
            """,
            "CREATE NONCLUSTERED INDEX IX_Customers_Name ON Sales.Customers (CustomerName) INCLUDE (CreditLimit)",
            "CREATE NONCLUSTERED INDEX IX_Customers_Active ON Sales.Customers (CustomerName) WHERE CreditLimit > 0",
            """
            CREATE TABLE Application.EmployeeRoles (
                RoleID     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_EmployeeRoles PRIMARY KEY CLUSTERED,
                PersonID   int NOT NULL,
                RoleName   nvarchar(50) NOT NULL,
                ValidFrom  datetime2(7) GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo    datetime2(7) GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = Application.EmployeeRolesHistory))
            """,
            "CREATE SEQUENCE Sales.OrderNumber AS int START WITH 1000 INCREMENT BY 1",
            "CREATE VIEW Sales.CustomerSummary AS SELECT CustomerID, CustomerName FROM Sales.Customers",
            "CREATE PROCEDURE Sales.GetCustomerCount AS SELECT COUNT(*) AS Cnt FROM Sales.Customers",
            "CREATE TRIGGER Sales.trg_Customers_Insert ON Sales.Customers AFTER INSERT AS BEGIN SET NOCOUNT ON; END",
            "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'People master table', " +
                "@level0type = N'SCHEMA', @level0name = N'Application', @level1type = N'TABLE', @level1name = N'People'",
            "EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Person full name', " +
                "@level0type = N'SCHEMA', @level0name = N'Application', @level1type = N'TABLE', @level1name = N'People', " +
                "@level2type = N'COLUMN', @level2name = N'FullName'",
            "INSERT Application.People (FullName, IsEmployee) VALUES ('Alice', 1), ('Bob', 0)",
            "INSERT Sales.Customers (CustomerName, PrimaryContactPersonID, CreditLimit) VALUES ('Acme', 1, 100), ('Globex', 2, 200)",
            "INSERT Sales.Orders (CustomerID, OrderReference) VALUES (1, 'PO-1'), (2, 'PO-2')",
            "INSERT Sales.OrderLines (OrderID, CustomerID) VALUES (1, 1), (2, 2)",
            "INSERT Application.EmployeeRoles (PersonID, RoleName) VALUES (1, 'Manager')",
            "CREATE LOGIN smo WITH PASSWORD = 'smo'",
        ];

        using var connection = sim.CreateDbConnection();
        connection.Open();
        foreach (var batch in batches)
        {
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            _ = command.ExecuteNonQuery();
        }
    }
}
