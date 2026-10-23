using Dapper;
using Microsoft.Data.SqlClient;

namespace BcMes.Server.Data;

/// <summary>
/// SQL Server 主库（工单 / SN / 检测记录 / 追溯 / 分钟产出桶）。
///
/// 与 SQLite 版的三个本质区别：
///   1. 主键不用 IDENTITY，用 SEQUENCE 按 5000 一档做 Hi-Lo 区间申请 ——
///      SqlBulkCopy 无法回读自增 ID，而记录头和检测值需要跨表引用同一个 Id。
///      写入线程每批只去数据库取一次区间，批内本地分配，无锁无往返。
///   2. cells / yield_minute 用 MERGE upsert（SQLite 是 ON CONFLICT，T-SQL 对应 MERGE）。
///   3. 连接池默认开启，读短连接 / 写长连接由调用方决定。
/// </summary>
public sealed class SqlServerDb
{
    public const int HiLoBlockSize = 5000;

    private readonly string _cs;
    private readonly string _masterCs;

    public SqlServerDb(string connectionString)
    {
        _cs = connectionString;
        // master 连接串用于建库：容器首次启动时 BcMes 库还不存在
        var csb = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        _masterCs = csb.ConnectionString;
    }

    public SqlConnection Open()
    {
        var conn = new SqlConnection(_cs);
        conn.Open();
        return conn;
    }

    public long NextHiLoBlock(SqlConnection conn, SqlTransaction? tx)
    {
        var baseId = conn.ExecuteScalar<long>("SELECT NEXT VALUE FOR dbo.seq_record;", transaction: tx);
        return baseId;
    }

    // ── DDL（幂等：每条自带 IF 判断）────────────────────────────────

    public void Initialize()
    {
        using (var master = new SqlConnection(_masterCs))
        {
            master.Execute("IF DB_ID(N'BcMes') IS NULL CREATE DATABASE BcMes;");
        }
        using var conn = Open();
        foreach (var sql in DdlScripts) conn.Execute(sql);
        Seed(conn);
    }

    private static readonly string[] DdlScripts =
    {
        """
        IF OBJECT_ID(N'dbo.products','U') IS NULL
        CREATE TABLE dbo.products(
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Code NVARCHAR(50) NOT NULL UNIQUE,
            Name NVARCHAR(100) NOT NULL,
            NominalCapacityAh FLOAT NOT NULL DEFAULT 0,
            NominalVoltageV FLOAT NOT NULL DEFAULT 0,
            CapacityLowerAh FLOAT NULL, CapacityUpperAh FLOAT NULL,
            AcirLowerMohm FLOAT NULL, AcirUpperMohm FLOAT NULL,
            OcvLowerV FLOAT NULL, OcvUpperV FLOAT NULL);
        """,
        """
        IF OBJECT_ID(N'dbo.operations','U') IS NULL
        CREATE TABLE dbo.operations(
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Code NVARCHAR(50) NOT NULL UNIQUE,
            Name NVARCHAR(100) NOT NULL,
            Sequence INT NOT NULL DEFAULT 0);
        """,
        """
        IF OBJECT_ID(N'dbo.inspection_items','U') IS NULL
        BEGIN
            CREATE TABLE dbo.inspection_items(
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(50) NOT NULL,
                Name NVARCHAR(100) NOT NULL,
                OperationCode NVARCHAR(50) NOT NULL,
                ValueType TINYINT NOT NULL DEFAULT 0,
                Unit NVARCHAR(20) NOT NULL DEFAULT '',
                Lower FLOAT NULL, Upper FLOAT NULL, Target FLOAT NULL,
                Mode TINYINT NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX ux_item_op ON dbo.inspection_items(OperationCode, Code);
        END
        """,
        """
        IF OBJECT_ID(N'dbo.equipment','U') IS NULL
        CREATE TABLE dbo.equipment(
            Id INT IDENTITY(1,1) PRIMARY KEY,
            Code NVARCHAR(50) NOT NULL UNIQUE,
            Name NVARCHAR(100) NOT NULL,
            Kind NVARCHAR(50) NOT NULL DEFAULT '',
            Endpoint NVARCHAR(200) NULL,
            ChannelCount INT NOT NULL DEFAULT 0,
            Enabled BIT NOT NULL DEFAULT 1);
        """,
        """
        IF OBJECT_ID(N'dbo.work_orders','U') IS NULL
        BEGIN
            CREATE TABLE dbo.work_orders(
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Code NVARCHAR(50) NOT NULL UNIQUE,
                BatchNo NVARCHAR(50) NOT NULL,
                ProductId INT NOT NULL,
                PlanQty INT NOT NULL DEFAULT 0,
                DoneQty INT NOT NULL DEFAULT 0,
                Status TINYINT NOT NULL DEFAULT 0,
                CreatedAt BIGINT NOT NULL);
            CREATE INDEX ix_wo_batch ON dbo.work_orders(BatchNo);
        END
        """,
        """
        IF OBJECT_ID(N'dbo.cells','U') IS NULL
        BEGIN
            CREATE TABLE dbo.cells(
                Id INT IDENTITY(1,1) PRIMARY KEY,
                Sn NVARCHAR(50) NOT NULL UNIQUE,
                BatchNo NVARCHAR(50) NOT NULL DEFAULT '',
                ProductId INT NOT NULL DEFAULT 1,
                WorkOrderCode NVARCHAR(50) NULL,
                CurrentOpCode NVARCHAR(50) NOT NULL DEFAULT 'WINDING',
                Status TINYINT NOT NULL DEFAULT 0,
                Grade NVARCHAR(10) NULL,
                CreatedAt BIGINT NOT NULL);
            CREATE INDEX ix_cells_batch ON dbo.cells(BatchNo);
            CREATE INDEX ix_cells_wo ON dbo.cells(WorkOrderCode);
            CREATE INDEX ix_cells_created ON dbo.cells(CreatedAt);
        END
        """,
        """
        IF OBJECT_ID(N'dbo.inspection_records','U') IS NULL
        BEGIN
            CREATE TABLE dbo.inspection_records(
                Id BIGINT NOT NULL PRIMARY KEY,        -- Hi-Lo 应用侧分配，非 IDENTITY
                CellSn NVARCHAR(50) NOT NULL,
                OperationCode NVARCHAR(50) NOT NULL,
                EquipmentCode NVARCHAR(50) NOT NULL,
                WorkOrderCode NVARCHAR(50) NULL,
                Result NVARCHAR(10) NOT NULL,
                FailItemCode NVARCHAR(50) NULL,
                Timestamp BIGINT NOT NULL);
            CREATE INDEX ix_rec_sn ON dbo.inspection_records(CellSn);
            CREATE INDEX ix_rec_ts ON dbo.inspection_records(Timestamp);
            CREATE INDEX ix_rec_op_ts ON dbo.inspection_records(OperationCode, Timestamp);
            CREATE INDEX ix_rec_eq_ts ON dbo.inspection_records(EquipmentCode, Timestamp);
        END
        """,
        """
        IF OBJECT_ID(N'dbo.inspection_values','U') IS NULL
        BEGIN
            CREATE TABLE dbo.inspection_values(
                Id BIGINT IDENTITY(1,1) PRIMARY KEY,
                RecordId BIGINT NOT NULL,
                ItemCode NVARCHAR(50) NOT NULL,
                ItemName NVARCHAR(100) NOT NULL DEFAULT '',
                Number FLOAT NULL,
                Text NVARCHAR(200) NULL,
                Unit NVARCHAR(20) NOT NULL DEFAULT '',
                Result NVARCHAR(10) NOT NULL DEFAULT 'OK');
            CREATE INDEX ix_val_rec ON dbo.inspection_values(RecordId);
            CREATE INDEX ix_val_item ON dbo.inspection_values(ItemCode, RecordId);
        END
        """,
        """
        IF OBJECT_ID(N'dbo.yield_minute','U') IS NULL
        CREATE TABLE dbo.yield_minute(
            Bucket BIGINT NOT NULL,
            OperationCode NVARCHAR(50) NOT NULL,
            EquipmentCode NVARCHAR(50) NOT NULL,
            Total INT NOT NULL DEFAULT 0,
            Ok INT NOT NULL DEFAULT 0,
            Ng INT NOT NULL DEFAULT 0,
            PRIMARY KEY CLUSTERED (Bucket, OperationCode, EquipmentCode));
        """,
        """
        IF OBJECT_ID(N'dbo.seq_record','SO') IS NULL
        CREATE SEQUENCE dbo.seq_record AS BIGINT START WITH 1 INCREMENT BY 5000;
        """,
    };

    private static void Seed(SqlConnection conn)
    {
        var exists = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM dbo.products");
        if (exists > 0) return;

        conn.Execute("""
        INSERT INTO dbo.products(Code,Name,NominalCapacityAh,NominalVoltageV,CapacityLowerAh,CapacityUpperAh,AcirLowerMohm,AcirUpperMohm,OcvLowerV,OcvUpperV) VALUES
        (N'LFP-100AH',N'方形铝壳磷酸铁锂 100Ah',100,3.2,98.0,103.0,0.30,0.65,3.290,3.320),
        (N'NCM-50AH',N'软包三元 50Ah',50,3.7,48.5,51.0,0.40,0.90,3.800,3.840);
        """);

        conn.Execute("""
        INSERT INTO dbo.operations(Code,Name,Sequence) VALUES
        (N'WINDING',N'卷绕',10),(N'WELDING',N'极耳焊接',20),(N'HELIUM',N'氦检测漏',30),(N'INJECTION',N'注液称重',40),
        (N'FORMATION',N'化成',50),(N'GRADING',N'分容',60),(N'OCV_ACIR',N'OCV/ACIR 测试',70),(N'VISION',N'外观/X-Ray',80);
        """);

        conn.Execute("""
        INSERT INTO dbo.inspection_items(Code,Name,OperationCode,ValueType,Unit,Lower,Upper,Target,Mode) VALUES
        (N'WELD_TENSION',N'极耳焊接拉力',N'WELDING',0,N'N',40,NULL,NULL,2),
        (N'HE_LEAK_RATE',N'氦检漏率',N'HELIUM',0,N'Pa·m3/s',NULL,5E-7,NULL,1),
        (N'INJ_WEIGHT',N'注液量',N'INJECTION',0,N'g',430,450,440,0),
        (N'END_VOLTAGE',N'化成结束电压',N'FORMATION',0,N'V',3.600,3.750,NULL,0),
        (N'CAPACITY',N'分容容量',N'GRADING',0,N'Ah',98.0,103.0,NULL,0),
        (N'OCV',N'开路电压',N'OCV_ACIR',0,N'V',3.290,3.320,NULL,0),
        (N'ACIR',N'交流内阻',N'OCV_ACIR',0,N'N:mΩ',NULL,0.65,NULL,1),
        (N'K_VALUE',N'自放电 K 值',N'OCV_ACIR',0,N'mV/d',NULL,0.15,NULL,1),
        (N'APPEARANCE',N'外观判定',N'VISION',1,N'',NULL,NULL,NULL,0);
        """);

        conn.Execute("""
        INSERT INTO dbo.equipment(Code,Name,Kind,Endpoint,ChannelCount,Enabled) VALUES
        (N'EQ-WELD-01',N'1#超声波焊接机',N'WELDING',N'http://127.0.0.1:5101',0,1),
        (N'EQ-HE-01',N'1#氦检机',N'HELIUM',N'http://127.0.0.1:5101',0,1),
        (N'EQ-INJ-01',N'1#注液机',N'INJECTION',N'http://127.0.0.1:5101',0,1),
        (N'EQ-FORM-01',N'1#化成柜',N'FORMATION',N'opc.tcp://127.0.0.1:4840/bcmes/simulator',8,1),
        (N'EQ-FORM-02',N'2#化成柜',N'FORMATION',N'opc.tcp://127.0.0.1:4840/bcmes/simulator',8,1),
        (N'EQ-GRD-01',N'1#分容柜',N'GRADING',N'http://127.0.0.1:5101',0,1),
        (N'EQ-OCV-01',N'1#OCV/ACIR 测试仪',N'OCV_ACIR',N'http://127.0.0.1:5101',0,1),
        (N'EQ-VIS-01',N'1#外观检测机',N'VISION',N'http://127.0.0.1:5101',0,1);
        """);

        conn.Execute("""
        IF NOT EXISTS (SELECT 1 FROM dbo.work_orders WHERE Code = N'WO-DEMO-001')
        INSERT INTO dbo.work_orders(Code,BatchNo,ProductId,PlanQty,DoneQty,Status,CreatedAt)
        VALUES(N'WO-DEMO-001',N'B20261022-001',1,5000,0,1,@now);
        """, new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    /// <summary>幂等创建演示工单（UTC 毫秒时间戳）。</summary>
    public void EnsureWorkOrder(string code, string batchNo, int productId, int planQty)
    {
        using var conn = Open();
        conn.Execute("""
            IF NOT EXISTS (SELECT 1 FROM dbo.work_orders WHERE Code = @code)
            INSERT INTO dbo.work_orders(Code,BatchNo,ProductId,PlanQty,DoneQty,Status,CreatedAt)
            VALUES(@code,@batch,@product,@plan,0,1,@now);
            """,
            new { code, batch = batchNo, product = productId, plan = planQty, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }
}
