using BcMes.Contracts;
using BcMes.Server.Cache;
using BcMes.Server.Data;
using BcMes.Server.Services;
using Dapper;
using StackExchange.Redis;

namespace BcMes.Server.Endpoints;

public static class MesEndpoints
{
    public static WebApplication MapMes(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { ok = true, ts = Ts.Now() }));

        app.MapGet("/api/queue", (WriteQueue q, SpecCache c) => Results.Ok(new
        {
            queuedRecords = q.QueuedRecords,
            queuedSamples = q.QueuedSamples,
            specItems = c.Count,
            specLoadedAt = c.LoadedAt
        }));

        app.MapGet("/api/spec", (SpecCache c, SqlServerDb db) =>
        {
            using var conn = db.Open();
            return Results.Ok(conn.Query<InspectionItem>(
                "SELECT Id, Code, Name, OperationCode, ValueType, Unit, Lower, Upper, Target, Mode " +
                "FROM dbo.inspection_items ORDER BY OperationCode, Code"));
        });

        app.MapGet("/api/equipment", (SqlServerDb db) =>
        {
            using var conn = db.Open();
            return Results.Ok(conn.Query<Equipment>(
                "SELECT Id, Code, Name, Kind, Endpoint, ChannelCount, Enabled FROM dbo.equipment ORDER BY Code"));
        });

        app.MapPost("/api/cache/reload", (SpecCache c) => { c.Load(); return Results.Ok(new { reloaded = c.Count }); });

        // ── 工位上传检测数据（人工录入或半自动设备走这条路）──
        app.MapPost("/api/detection", (DetectionUpload up, WriteQueue q) =>
        {
            if (string.IsNullOrWhiteSpace(up.CellSn)) return Results.BadRequest("CellSn 不能为空");
            if (string.IsNullOrWhiteSpace(up.OperationCode)) return Results.BadRequest("OperationCode 不能为空");

            var ok = q.TryEnqueueRecord(new PendingRecord(
                up.CellSn, up.OperationCode, up.EquipmentCode ?? "",
                up.WorkOrderCode, up.Timestamp == 0 ? Ts.Now() : up.Timestamp,
                up.Values?.ToList() ?? new List<TestValueDto>()));

            return ok ? Results.Accepted() : Results.StatusCode(503);
        });

        app.MapGet("/api/stats", (SqlServerDb db, IConnectionMultiplexer? redis) =>
            Results.Ok(StatsPump.Read(db, redis)));

        app.MapGet("/api/records", (int take, SqlServerDb db) =>
        {
            take = Math.Clamp(take, 1, 500);
            using var conn = db.Open();
            var recs = conn.Query<InspectionRecord>(
                "SELECT TOP (@take) Id, CellSn, OperationCode, EquipmentCode, WorkOrderCode, Result, FailItemCode, Timestamp " +
                "FROM dbo.inspection_records ORDER BY Id DESC", new { take }).ToList();
            if (recs.Count == 0) return Results.Ok(recs);

            var ids = recs.Select(r => r.Id).ToList();
            var vals = conn.Query<(int RecordId, string ItemCode, string ItemName, double? Number, string? Text, string Unit, string Result)>(
                $"SELECT RecordId, ItemCode, ItemName, Number, Text, Unit, Result FROM dbo.inspection_values " +
                $"WHERE RecordId IN ({string.Join(',', ids)})").ToList();
            foreach (var r in recs)
            {
                r.Values = vals.Where(v => v.RecordId == r.Id)
                    .Select(v => new InspectionValue
                    {
                        RecordId = v.RecordId, ItemCode = v.ItemCode, ItemName = v.ItemName,
                        Number = v.Number, Text = v.Text, Unit = v.Unit, Result = v.Result
                    }).ToList();
            }
            return Results.Ok(recs);
        });

        // ── 数据查询：条件组合查询检测记录（时间范围 / 工段 / 设备 / SN 模糊 / 工序 / 结果，全部可选）──
        app.MapGet("/api/records/query", (string? sn, string? op, string? result, string? stage, string? eq,
            long? from, long? to, int take, SqlServerDb db) =>
        {
            take = Math.Clamp(take, 1, 500);
            using var conn = db.Open();

            var p = new DynamicParameters(new { take, sn = $"%{sn?.Trim()}%", op, result, eq });
            var w = "1=1";
            if (!string.IsNullOrWhiteSpace(sn)) w += " AND CellSn LIKE @sn";
            if (!string.IsNullOrWhiteSpace(op)) w += " AND OperationCode = @op";
            if (!string.IsNullOrWhiteSpace(result)) w += " AND Result = @result";
            if (!string.IsNullOrWhiteSpace(eq)) w += " AND EquipmentCode = @eq";
            if (from > 0) { w += " AND Timestamp >= @from"; p.Add("from", from); }
            if (to > 0) { w += " AND Timestamp <= @to"; p.Add("to", to); }
            if (!string.IsNullOrWhiteSpace(stage) && Codes.StageOps.TryGetValue(stage, out var ops))
            {
                w += " AND OperationCode IN @ops";
                p.Add("ops", ops);
            }

            var recs = conn.Query<InspectionRecord>(
                $"SELECT TOP (@take) Id, CellSn, OperationCode, EquipmentCode, WorkOrderCode, Result, FailItemCode, Timestamp " +
                $"FROM dbo.inspection_records WHERE {w} ORDER BY Id DESC", p).ToList();
            if (recs.Count == 0) return Results.Ok(recs);

            var ids = recs.Select(r => r.Id).ToList();
            var vals = conn.Query<(int RecordId, string ItemCode, string ItemName, double? Number, string? Text, string Unit, string Result)>(
                $"SELECT RecordId, ItemCode, ItemName, Number, Text, Unit, Result FROM dbo.inspection_values " +
                $"WHERE RecordId IN ({string.Join(',', ids)})").ToList();
            foreach (var r in recs)
            {
                r.Values = vals.Where(v => v.RecordId == r.Id)
                    .Select(v => new InspectionValue
                    {
                        RecordId = v.RecordId, ItemCode = v.ItemCode, ItemName = v.ItemName,
                        Number = v.Number, Text = v.Text, Unit = v.Unit, Result = v.Result
                    }).ToList();
            }
            return Results.Ok(recs);
        });

        // ── 单件追溯：SN → 全工序全检测值 ──
        app.MapGet("/api/trace/{sn}", (string sn, SqlServerDb db) =>
        {
            using var conn = db.Open();
            var cell = conn.QuerySingleOrDefault<Cell>(
                "SELECT Id, Sn, BatchNo, ProductId, WorkOrderCode, CurrentOpCode, Status, Grade, CreatedAt " +
                "FROM dbo.cells WHERE Sn = @sn", new { sn });
            if (cell is null) return Results.NotFound(new { error = $"未找到电芯 {sn}" });

            var recs = conn.Query<(long Id, string OperationCode, string EquipmentCode, string Result, string? FailItemCode, long Timestamp)>(
                "SELECT Id, OperationCode, EquipmentCode, Result, FailItemCode, Timestamp " +
                "FROM dbo.inspection_records WHERE CellSn = @sn ORDER BY Timestamp", new { sn }).ToList();

            var dto = new List<InspectionRecordDto>();
            foreach (var r in recs)
            {
                var vs = conn.Query<ValueResultDto>(
                    "SELECT ItemCode, ItemName, Number, Text, Unit, Result FROM dbo.inspection_values WHERE RecordId = @rid",
                    new { rid = r.Id }).ToList();
                dto.Add(new InspectionRecordDto(r.Id, r.OperationCode, r.EquipmentCode, r.Result, r.FailItemCode, r.Timestamp, vs));
            }

            string productCode = conn.QuerySingleOrDefault<string>(
                "SELECT Code FROM dbo.products WHERE Id = @id", new { id = cell.ProductId }) ?? "";

            return Results.Ok(new TraceResultDto(
                cell.Sn, cell.BatchNo, cell.WorkOrderCode, productCode,
                cell.Status, cell.Grade, cell.CreatedAt, dto));
        });

        // ── 化成曲线：走 TDengine（时序库），标签过滤按 SN ──
        app.MapGet("/api/curve/{sn}", async (string sn, TdEngineWriter td) =>
            Results.Ok(await td.QueryCurveAsync(sn)));

        // ── SPC 统计：可统计的数值型检测项清单（含样本数，供前端下拉框）──
        app.MapGet("/api/spc/items", (SqlServerDb db) =>
        {
            using var conn = db.Open();
            var items = conn.Query<(string ItemCode, string ItemName, string OperationCode, string Unit, int SampleCount)>(
                @"SELECT v.ItemCode, MAX(v.ItemName) AS ItemName,
                         MAX(r.OperationCode) AS OperationCode,
                         MAX(i.Unit) AS Unit, COUNT(*) AS SampleCount
                  FROM dbo.inspection_values v
                  JOIN dbo.inspection_records r ON r.Id = v.RecordId
                  JOIN dbo.inspection_items  i ON i.Code = v.ItemCode
                  WHERE v.Number IS NOT NULL
                  GROUP BY v.ItemCode
                  HAVING COUNT(*) >= 10
                  ORDER BY SampleCount DESC").ToList();
            return Results.Ok(items.Select(x =>
                new SpcItemDto(x.ItemCode, x.ItemName, x.OperationCode, x.Unit, x.SampleCount)));
        });

        // ── SPC 控制图：某检测项最近 N 个样本的 I-MR 图数据 ──
        app.MapGet("/api/spc/{itemCode}", (string itemCode, int take, SqlServerDb db) =>
        {
            take = Math.Clamp(take, 20, 500);
            using var conn = db.Open();

            var item = conn.QuerySingleOrDefault<(string Name, string Unit, double? Lower, double? Upper)>(
                "SELECT Name, Unit, Lower, Upper FROM dbo.inspection_items WHERE Code = @code",
                new { code = itemCode });
            if (item.Name is null) return Results.NotFound(new { error = $"未找到检测项 {itemCode}" });

            // 时间正序的单值序列（I-MR 图）——取最近 N 条再反转；带上 SN 供前端双击追溯
            var rows = conn.Query<(long Ts, double Value, string? Sn)>(
                @"SELECT TOP (@take) r.Timestamp AS Ts, CAST(v.Number AS float) AS Value, r.CellSn AS Sn
                  FROM dbo.inspection_values v
                  JOIN dbo.inspection_records r ON r.Id = v.RecordId
                  WHERE v.ItemCode = @code AND v.Number IS NOT NULL
                  ORDER BY r.Timestamp DESC", new { take, code = itemCode }).ToList();
            rows.Reverse();
            if (rows.Count < 10)
                return Results.BadRequest(new { error = $"样本不足（{rows.Count} 个，至少 10 个）" });

            var chart = SpcCalculator.Build(
                itemCode, item.Name, item.Unit, item.Lower, item.Upper, rows);
            return Results.Ok(chart);
        });

        // ── 质量分析面板：不良帕累托 + 工序不良率 + 各检测项稳定性 ──
        app.MapGet("/api/quality/summary", (SqlServerDb db) =>
        {
            using var conn = db.Open();

            // 总量与不良总数
            var head = conn.QuerySingle<(long Total, long Ng)>(
                @"SELECT COUNT(*), ISNULL(SUM(CASE WHEN Result = @ng THEN 1 ELSE 0 END), 0)
                  FROM dbo.inspection_records", new { ng = Codes.ResultNg });

            // 1. 不良帕累托：按失效检测项聚合（二八定律，找出头号不良）
            var byItem = conn.Query<(string ItemCode, long Count)>(
                @"SELECT ISNULL(FailItemCode, N'(未记录)') AS ItemCode, COUNT(*) AS Cnt
                  FROM dbo.inspection_records
                  WHERE Result = @ng
                  GROUP BY FailItemCode
                  ORDER BY COUNT(*) DESC", new { ng = Codes.ResultNg }).ToList();
            var itemNames = conn.Query<(string Code, string Name)>(
                "SELECT Code, Name FROM dbo.inspection_items")
                .ToDictionary(x => x.Code, x => x.Name);
            var ngItems = byItem.Select(x => new NgItemDto(
                x.ItemCode,
                itemNames.GetValueOrDefault(x.ItemCode, x.ItemCode == "(未记录)" ? "未记录失效项" : "未定义检测项"),
                x.Count,
                head.Total > 0 ? Math.Round(x.Count * 100.0 / head.Total, 2) : 0)).ToList();

            // 2. 工序不良率：哪道工序在流失直通率
            var byOp = conn.Query<(string OperationCode, long Total, long Ng)>(
                @"SELECT OperationCode, COUNT(*),
                         ISNULL(SUM(CASE WHEN Result = @ng THEN 1 ELSE 0 END), 0)
                  FROM dbo.inspection_records
                  GROUP BY OperationCode
                  ORDER BY OperationCode", new { ng = Codes.ResultNg }).ToList();
            var ngOps = byOp.Select(x => new NgOpDto(
                x.OperationCode, x.Total, x.Ng,
                x.Total > 0 ? Math.Round(x.Ng * 100.0 / x.Total, 2) : 0)).ToList();

            // 3. 稳定性总览：每个数值型检测项的均值 / σ / Cp / Cpk / 超规格数。
            //    σ 用样本标准差 STDEV（与 I-MR 图的 MR̄/d2 是两种估计，大样本下趋同）。
            var rows = conn.Query<(string ItemCode, string ItemName, string Unit,
                double? Lsl, double? Usl, int Samples, double Mean, double Sigma, int OutOfSpec)>(
                @"SELECT v.ItemCode,
                         MAX(v.ItemName)          AS ItemName,
                         MAX(i.Unit)              AS Unit,
                         MAX(i.Lower)             AS Lsl,
                         MAX(i.Upper)             AS Usl,
                         COUNT(*)                 AS Samples,
                         AVG(CAST(v.Number AS float))                  AS Mean,
                         ISNULL(STDEV(CAST(v.Number AS float)), 0)     AS Sigma,
                         SUM(CASE WHEN (i.Lower IS NOT NULL AND v.Number < i.Lower)
                                    OR (i.Upper IS NOT NULL AND v.Number > i.Upper)
                                  THEN 1 ELSE 0 END)              AS OutOfSpec
                  FROM dbo.inspection_values v
                  JOIN dbo.inspection_records r ON r.Id = v.RecordId
                  JOIN dbo.inspection_items  i ON i.Code = v.ItemCode
                  WHERE v.Number IS NOT NULL
                  GROUP BY v.ItemCode
                  HAVING COUNT(*) >= 10
                  ORDER BY COUNT(*) DESC").ToList();

            var stability = rows.Select(x =>
            {
                double? cp = null, cpk = null;
                if (x.Sigma > 1e-12)
                {
                    if (x.Lsl.HasValue && x.Usl.HasValue)
                    {
                        cp = (x.Usl.Value - x.Lsl.Value) / (6 * x.Sigma);
                        cpk = Math.Min(x.Usl.Value - x.Mean, x.Mean - x.Lsl.Value) / (3 * x.Sigma);
                    }
                    // 单侧规格：只对存在的限算单边 Cpk（拉力只看下限 / 漏率只看上限）
                    else if (x.Usl.HasValue) cpk = (x.Usl.Value - x.Mean) / (3 * x.Sigma);
                    else if (x.Lsl.HasValue) cpk = (x.Mean - x.Lsl.Value) / (3 * x.Sigma);
                }
                return new StabilityDto(x.ItemCode, x.ItemName, x.Unit,
                    x.Samples, x.Mean, x.Sigma, x.Lsl, x.Usl, cp, cpk, x.OutOfSpec);
            }).ToList();

            return Results.Ok(new QualitySummaryDto(
                head.Total, head.Ng,
                head.Total > 0 ? Math.Round(head.Ng * 100.0 / head.Total, 2) : 0,
                ngItems, ngOps, stability));
        });

        // ── 不良明细下钻：点击帕累托检测项 / 工序节点后，返回这些 NG 电芯的逐条记录 ──
        app.MapGet("/api/quality/ng-records", (string? itemCode, string? opCode, int take, SqlServerDb db) =>
        {
            take = Math.Clamp(take, 1, 200);
            using var conn = db.Open();

            var where = "r.Result = @ng";
            // "(未记录)" 是帕累托里对 FailItemCode IS NULL 的哨兵显示值
            if (!string.IsNullOrEmpty(itemCode))
                where += itemCode == "(未记录)" ? " AND r.FailItemCode IS NULL" : " AND r.FailItemCode = @item";
            if (!string.IsNullOrEmpty(opCode)) where += " AND r.OperationCode = @op";

            var recs = conn.Query<(long Id, string CellSn, string OperationCode, string EquipmentCode,
                string? FailItemCode, long Timestamp)>(
                $@"SELECT TOP (@take) r.Id, r.CellSn, r.OperationCode, r.EquipmentCode, r.FailItemCode, r.Timestamp
                   FROM dbo.inspection_records r
                   WHERE {where}
                   ORDER BY r.Timestamp DESC",
                new { take, ng = Codes.ResultNg, item = itemCode, op = opCode }).ToList();
            if (recs.Count == 0) return Results.Ok(Array.Empty<NgDetailDto>());

            var ids = recs.Select(r => r.Id).ToList();
            var vals = conn.Query<(int RecordId, string ItemCode, string ItemName, double? Number, string? Text, string Unit)>(
                $"SELECT RecordId, ItemCode, ItemName, Number, Text, Unit FROM dbo.inspection_values " +
                $"WHERE RecordId IN ({string.Join(',', ids)})").ToList();
            var names = conn.Query<(string Code, string Name)>("SELECT Code, Name FROM dbo.inspection_items")
                .ToDictionary(x => x.Code, x => x.Name);

            var dto = recs.Select(r =>
            {
                var detail = string.Join("   ", vals.Where(v => v.RecordId == r.Id).Select(v =>
                {
                    if (v.Number.HasValue)
                    {
                        var num = v.Number.Value;
                        var fmt = Math.Abs(num) < 1e-3 || Math.Abs(num) >= 1e4 ? "0.###e+0" : "0.####";
                        return $"{v.ItemCode}={num.ToString(fmt)}{v.Unit}";
                    }
                    return $"{v.ItemCode}={v.Text}";
                }));
                return new NgDetailDto(r.Id, r.CellSn, r.OperationCode, r.EquipmentCode,
                    r.FailItemCode, r.FailItemCode is null ? null : names.GetValueOrDefault(r.FailItemCode, r.FailItemCode),
                    r.Timestamp, detail);
            }).ToList();

            return Results.Ok(dto);
        });

        return app;
    }
}
