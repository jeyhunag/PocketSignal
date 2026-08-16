using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Data.Entities;
using PocketSignal.Api.Models.Admin;
using PocketSignal.Api.Services.Admin;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly IAdminRuntimeSettingsService _settingsService;
    private readonly IWebHostEnvironment _environment;
    private readonly PocketSignalDbContext _dbContext;

    public AdminController(
        IAdminRuntimeSettingsService settingsService,
        IWebHostEnvironment environment,
        PocketSignalDbContext dbContext)
    {
        _settingsService = settingsService;
        _environment = environment;
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetAsync(cancellationToken);

        var html = BuildHtml(settings);

        return Content(
            html,
            "text/html; charset=utf-8",
            Encoding.UTF8);
    }

    [HttpGet("settings")]
    public async Task<ActionResult<AdminRuntimeSettings>> GetSettings(
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpPost("settings")]
    public async Task<ActionResult<AdminRuntimeSettings>> UpdateSettings(
        [FromBody] AdminRuntimeSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.UpdateAsync(
            request,
            cancellationToken);

        return Ok(settings);
    }

    [HttpGet("reports")]
    public async Task<IActionResult> GetReports(
        [FromQuery] string? date,
        CancellationToken cancellationToken)
    {
        var reportDateAz = ParseAzerbaijanDateOrToday(date);
        var range = GetUtcRangeForAzerbaijanDate(reportDateAz);

        var binaryTrades = await _dbContext.BinaryTradeResults
            .AsNoTracking()
            .Where(x =>
                x.CreatedAtUtc >= range.StartUtc &&
                x.CreatedAtUtc < range.EndUtc)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var forexTrades = await _dbContext.ForexTradeResults
            .AsNoTracking()
            .Where(x =>
                x.CreatedAtUtc >= range.StartUtc &&
                x.CreatedAtUtc < range.EndUtc)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            dateAz = reportDateAz.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            binary = BuildBinaryReport(binaryTrades),
            forex = BuildForexReport(forexTrades)
        });
    }

    [HttpPost("reports/binary/delete-day")]
    public async Task<IActionResult> DeleteBinaryDay(
        [FromBody] ReportDateRequest request,
        CancellationToken cancellationToken)
    {
        var reportDateAz = ParseAzerbaijanDateOrToday(request.Date);
        var range = GetUtcRangeForAzerbaijanDate(reportDateAz);

        var trades = await _dbContext.BinaryTradeResults
            .Where(x =>
                x.CreatedAtUtc >= range.StartUtc &&
                x.CreatedAtUtc < range.EndUtc)
            .ToListAsync(cancellationToken);

        _dbContext.BinaryTradeResults.RemoveRange(trades);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            removed = trades.Count,
            message = $"{trades.Count} Binary nəticə silindi."
        });
    }

    [HttpPost("reports/forex/delete-day")]
    public async Task<IActionResult> DeleteForexDay(
        [FromBody] ReportDateRequest request,
        CancellationToken cancellationToken)
    {
        var reportDateAz = ParseAzerbaijanDateOrToday(request.Date);
        var range = GetUtcRangeForAzerbaijanDate(reportDateAz);

        var signals = await _dbContext.ForexSignals
            .Include(x => x.StrategyScores)
            .Include(x => x.TradeResult)
            .Where(x =>
                x.CreatedAtUtc >= range.StartUtc &&
                x.CreatedAtUtc < range.EndUtc)
            .ToListAsync(cancellationToken);

        _dbContext.ForexSignals.RemoveRange(signals);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            removed = signals.Count,
            message = $"{signals.Count} Forex signal/nəticə silindi."
        });
    }

    [HttpPost("reports/binary/delete/{id}")]
    public async Task<IActionResult> DeleteBinaryTrade(
        string id,
        CancellationToken cancellationToken)
    {
        var trade = await _dbContext.BinaryTradeResults
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (trade == null)
            return NotFound(new { message = "Binary nəticə tapılmadı." });

        _dbContext.BinaryTradeResults.Remove(trade);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Binary nəticə silindi." });
    }

    [HttpPost("reports/forex/delete/{id:guid}")]
    public async Task<IActionResult> DeleteForexTrade(
        Guid id,
        CancellationToken cancellationToken)
    {
        var trade = await _dbContext.ForexTradeResults
            .Include(x => x.ForexSignal)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (trade == null)
            return NotFound(new { message = "Forex nəticə tapılmadı." });

        if (trade.ForexSignal != null)
        {
            var signal = await _dbContext.ForexSignals
                .Include(x => x.StrategyScores)
                .Include(x => x.TradeResult)
                .FirstOrDefaultAsync(x => x.Id == trade.ForexSignalId, cancellationToken);

            if (signal != null)
            {
                _dbContext.ForexSignals.Remove(signal);
            }
            else
            {
                _dbContext.ForexTradeResults.Remove(trade);
            }
        }
        else
        {
            _dbContext.ForexTradeResults.Remove(trade);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Forex nəticə silindi." });
    }

    [HttpGet("charts/binary")]
    public ActionResult<List<object>> GetLatestBinaryCharts()
    {
        var charts = GetLatestChartFiles("binary-charts")
            .Select(x => new
            {
                fileName = x.FileName,
                url = x.Url,
                createdAtUtc = x.CreatedAtUtc
            })
            .Cast<object>()
            .ToList();

        return Ok(charts);
    }

    [HttpGet("charts/forex")]
    public ActionResult<List<object>> GetLatestForexCharts()
    {
        var charts = GetLatestChartFiles("forex-charts")
            .Select(x => new
            {
                fileName = x.FileName,
                url = x.Url,
                createdAtUtc = x.CreatedAtUtc
            })
            .Cast<object>()
            .ToList();

        return Ok(charts);
    }

    [HttpPost("charts/delete")]
    public IActionResult DeleteChart([FromBody] AdminChartDeleteRequest request)
    {
        if (request == null)
            return BadRequest(new { message = "Request bosdur." });

        var deleteResult = TryDeleteChart(
            request.FolderName,
            request.FileName);

        if (!deleteResult.Success)
            return BadRequest(new { message = deleteResult.Message });

        return Ok(new { message = deleteResult.Message });
    }

    [HttpPost("charts/clear")]
    public IActionResult ClearCharts([FromBody] AdminChartClearRequest request)
    {
        if (request == null)
            return BadRequest(new { message = "Request bosdur." });

        if (!IsAllowedChartFolder(request.FolderName))
            return BadRequest(new { message = "Chart folder icaze verilmir." });

        var chartDirectory = GetChartDirectory(request.FolderName);

        if (!Directory.Exists(chartDirectory))
        {
            return Ok(new
            {
                removed = 0,
                message = "Silinecek chart tapilmadi."
            });
        }

        var removed = 0;

        foreach (var file in Directory.GetFiles(chartDirectory, "*.png"))
        {
            try
            {
                System.IO.File.Delete(file);
                removed++;
            }
            catch
            {
                // Bir fayl silinmese, digerlerine davam edirik.
            }
        }

        return Ok(new
        {
            removed,
            message = $"{removed} chart silindi."
        });
    }

    private string BuildHtml(AdminRuntimeSettings settings)
    {
        var binaryRadios = BuildRadioButtons(
            "binaryActiveSymbol",
            settings.BinarySymbols,
            settings.BinaryActiveSymbol);

        var binaryChecked = settings.BinaryEnabled ? "checked" : "";
        var btf1Checked = settings.BinaryTimeframe == "1min" ? "checked" : "";
        var btf5Checked = settings.BinaryTimeframe == "5min" ? "checked" : "";
        var btf15Checked = (settings.BinaryTimeframe == "15min" || string.IsNullOrWhiteSpace(settings.BinaryTimeframe)) ? "checked" : "";
        var forexChecked = settings.ForexEnabled ? "checked" : "";
        var tf1Checked = settings.ForexTimeframe == "1min" ? "checked" : "";
        var tf5Checked = settings.ForexTimeframe == "5min" ? "checked" : "";
        var tf15Checked = (settings.ForexTimeframe == "15min" || string.IsNullOrWhiteSpace(settings.ForexTimeframe)) ? "checked" : "";

        var mt5ActiveChecked = settings.Mt5AutoTradeEnabled ? "checked" : "";
        var mt5PassiveChecked = settings.Mt5AutoTradeEnabled ? "" : "checked";

        var mt5Tp1Checked = settings.Mt5TakeProfitMode == "TP1" ? "checked" : "";
        var mt5Tp2Checked = settings.Mt5TakeProfitMode == "TP2" ? "checked" : "";

        var mt5DemoChecked = settings.Mt5DemoOnly ? "checked" : "";
        var mt5OnePositionChecked = settings.Mt5OnePositionPerSymbol ? "checked" : "";

        var mt5LotSizeValue = settings.Mt5LotSize.ToString(
            "0.##",
            CultureInfo.InvariantCulture);

        var activeForexSymbolsText = settings.ForexActiveSymbols.Count > 0
            ? string.Join(", ", settings.ForexActiveSymbols)
            : settings.ForexActiveSymbol;

        var firstForexSymbol = settings.ForexActiveSymbols.Count > 0
            ? settings.ForexActiveSymbols.First()
            : settings.ForexActiveSymbol;

        var binaryStatusUrl =
            $"/api/market/status?symbol={WebUtility.UrlEncode(settings.BinaryActiveSymbol)}";

        var forexStatusUrl =
            $"/api/forex/status?symbol={WebUtility.UrlEncode(firstForexSymbol)}";

        var todayAz = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow,
                GetAzerbaijanTimeZone())
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var binaryChartCards = BuildChartCards(
            "binary-charts",
            "Hələ Binary chart yaradılmayıb.",
            "Real Binary LONG/SHORT signal gələndə chart burada görünəcək.");

        var forexChartCards = BuildChartCards(
            "forex-charts",
            "Hələ Forex chart yaradılmayıb.",
            "Real Forex LONG/SHORT signal gələndə chart burada görünəcək.");

        return $$"""
<!DOCTYPE html>
<html lang="az">
<head>
    <meta charset="utf-8" />
    <title>PocketSignal Admin</title>
    <style>
        body {
            background: #101418;
            color: #f4f4f5;
            font-family: Arial, sans-serif;
            margin: 0;
            padding: 24px;
        }

        .container {
            max-width: 1250px;
            margin: 0 auto;
        }

        h1 {
            margin-top: 0;
            font-size: 30px;
        }

        .grid {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 20px;
        }

        .card, .status, .chart-section, .report-section {
            background: #181f26;
            border: 1px solid #2b3540;
            border-radius: 16px;
            padding: 20px;
            box-shadow: 0 8px 24px rgba(0,0,0,0.25);
        }

        .status, .chart-section, .report-section {
            margin-top: 20px;
        }

        .card h2, .status h2, .chart-section h2, .report-section h2 {
            margin-top: 0;
            font-size: 22px;
        }

        .enabled-row {
            display: flex;
            align-items: center;
            gap: 10px;
            margin-bottom: 18px;
            font-size: 17px;
            flex-wrap: wrap;
        }

        .symbols {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 10px;
        }

        .symbol-option {
            background: #11171d;
            border: 1px solid #2b3540;
            border-radius: 12px;
            padding: 10px;
            cursor: pointer;
        }

        .symbol-option input {
            margin-right: 8px;
        }

        .actions {
            margin-top: 22px;
            display: flex;
            gap: 12px;
            align-items: center;
            flex-wrap: wrap;
        }

        button {
            background: #22c55e;
            color: #08110b;
            border: none;
            border-radius: 12px;
            padding: 12px 18px;
            font-weight: bold;
            cursor: pointer;
        }

        button:hover {
            background: #16a34a;
        }

        .danger-button {
            background: #ef4444;
            color: white;
        }

        .danger-button:hover {
            background: #dc2626;
        }

        .small-button {
            padding: 8px 12px;
            border-radius: 10px;
            font-size: 13px;
        }

        .input-control, .select-control {
            padding: 10px;
            border-radius: 10px;
            border: 1px solid #2b3540;
            background: #11171d;
            color: white;
            width: 150px;
        }

        a {
            color: #7dd3fc;
            text-decoration: none;
        }

        a:hover {
            text-decoration: underline;
        }

        .muted {
            color: #a1a1aa;
        }

        .success {
            color: #22c55e;
            font-weight: bold;
        }

        .danger {
            color: #ef4444;
            font-weight: bold;
        }

        .warning {
            color: #fbbf24;
            font-weight: bold;
        }

        .hint {
            color: #a1a1aa;
            font-size: 14px;
            margin-top: -6px;
            margin-bottom: 14px;
        }

        .chart-section-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 12px;
            flex-wrap: wrap;
            margin-bottom: 10px;
        }

        .chart-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
            gap: 14px;
        }

        .chart-card {
            background: #11171d;
            border: 1px solid #2b3540;
            border-radius: 16px;
            padding: 10px;
        }

        .chart-card img {
            width: 100%;
            max-height: 180px;
            object-fit: contain;
            border-radius: 12px;
            border: 1px solid #2b3540;
            display: block;
            background: #0f151b;
        }

        .chart-title {
            font-size: 12px;
            color: #d4d4d8;
            margin-bottom: 8px;
            word-break: break-all;
            line-height: 1.3;
        }

        .chart-card-footer {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 10px;
            margin-top: 8px;
        }

        .chart-date {
            font-size: 12px;
            color: #a1a1aa;
        }

        .report-controls {
            display: flex;
            gap: 12px;
            align-items: center;
            flex-wrap: wrap;
            margin-bottom: 18px;
        }

        .summary-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
            gap: 12px;
            margin-bottom: 18px;
        }

        .summary-card {
            background: #11171d;
            border: 1px solid #2b3540;
            border-radius: 14px;
            padding: 14px;
        }

        .summary-card .label {
            color: #a1a1aa;
            font-size: 13px;
        }

        .summary-card .value {
            font-size: 24px;
            font-weight: bold;
            margin-top: 4px;
        }

        table {
            width: 100%;
            border-collapse: collapse;
            margin-top: 12px;
            background: #11171d;
            border-radius: 12px;
            overflow: hidden;
        }

        th, td {
            border-bottom: 1px solid #2b3540;
            padding: 10px;
            text-align: left;
            font-size: 13px;
        }

        th {
            color: #d4d4d8;
            background: #0f151b;
        }

        .section-title {
            margin-top: 24px;
            margin-bottom: 8px;
            font-size: 19px;
        }

        @media (max-width: 850px) {
            .grid {
                grid-template-columns: 1fr;
            }

            .symbols {
                grid-template-columns: 1fr;
            }

            .chart-grid {
                grid-template-columns: 1fr;
            }
        }
    </style>
</head>
<body>
<div class="container">
    <h1>📊 PocketSignal Admin</h1>

    <div class="grid">
        <div class="card">
            <h2>Binary Bot</h2>

            <label class="enabled-row">
                <input type="checkbox" id="binaryEnabled" {{binaryChecked}} />
                Binary aktivdir
            </label>

            <p class="hint" style="margin-top:12px;">Signal timeframe (analiz + interval):</p>
            <div class="timeframe-row" style="display:flex; gap:16px; margin-bottom:12px;">
                <label><input type="radio" name="binaryTimeframe" value="1min" {{btf1Checked}} /> 1M (15 dəq siqnal)</label>
                <label><input type="radio" name="binaryTimeframe" value="5min" {{btf5Checked}} /> 5M (30 dəq siqnal)</label>
                <label><input type="radio" name="binaryTimeframe" value="15min" {{btf15Checked}} /> 15M (30 dəq siqnal)</label>
            </div>

            <div class="symbols">
                {{BuildCheckboxes("binaryActiveSymbols", settings.BinarySymbols, settings.BinaryActiveSymbols)}}
            </div>
        </div>

        <div class="card">
            <h2>Forex Bot</h2>

            <label class="enabled-row">
                <input type="checkbox" id="forexEnabled" {{forexChecked}} />
                Forex aktivdir
            </label>

            <p class="hint">
                Bir və ya bir neçə Forex / Gold / Crypto cütü seçə bilərsən.
            </p>

            <p class="hint" style="margin-top:12px;">Signal timeframe (analiz + interval):</p>
            <div class="timeframe-row" style="display:flex; gap:16px; margin-bottom:12px;">
                <label><input type="radio" name="forexTimeframe" value="1min" {{tf1Checked}} /> 1M (15 dəq siqnal)</label>
                <label><input type="radio" name="forexTimeframe" value="5min" {{tf5Checked}} /> 5M (30 dəq siqnal)</label>
                <label><input type="radio" name="forexTimeframe" value="15min" {{tf15Checked}} /> 15M (30 dəq siqnal)</label>
            </div>

            <div class="symbols">
                {{BuildCheckboxes("forexActiveSymbols", settings.ForexSymbols, settings.ForexActiveSymbols)}}
            </div>
        </div>
    </div>

    <div class="status">
        <h2>MT5 Auto Trade</h2>

        <div class="enabled-row">
            <label>
                <input type="radio" name="mt5AutoTradeEnabled" value="true" {{mt5ActiveChecked}} />
                MT5 avtomatik trade aktiv
            </label>

            <label>
                <input type="radio" name="mt5AutoTradeEnabled" value="false" {{mt5PassiveChecked}} />
                MT5 avtomatik trade deaktiv
            </label>
        </div>

        <div class="grid">
            <div>
                <label class="enabled-row">
                    Lot Size:
                    <input
                        type="number"
                        id="mt5LotSize"
                        min="0.01"
                        step="0.01"
                        value="{{mt5LotSizeValue}}"
                        class="input-control" />
                </label>

                <label class="enabled-row">
                    Minimum Confidence:
                    <input
                        type="number"
                        id="mt5MinimumConfidence"
                        min="50"
                        max="99"
                        step="1"
                        value="{{settings.Mt5MinimumConfidence}}"
                        class="input-control" />
                </label>

                <label class="enabled-row">
                    Minimum Grade:
                    <select id="mt5MinimumGrade" class="select-control">
                        <option value="B" {{(settings.Mt5MinimumGrade == "B" ? "selected" : "")}}>B</option>
                        <option value="A" {{(settings.Mt5MinimumGrade == "A" ? "selected" : "")}}>A</option>
                        <option value="A+" {{(settings.Mt5MinimumGrade == "A+" ? "selected" : "")}}>A+</option>
                    </select>
                </label>
            </div>

            <div>
                <div class="enabled-row">
                    Take Profit:
                    <label>
                        <input type="radio" name="mt5TakeProfitMode" value="TP1" {{mt5Tp1Checked}} />
                        TP1
                    </label>

                    <label>
                        <input type="radio" name="mt5TakeProfitMode" value="TP2" {{mt5Tp2Checked}} />
                        TP2
                    </label>
                </div>

                <label class="enabled-row">
                    Cooldown dəqiqə:
                    <input
                        type="number"
                        id="mt5CooldownMinutes"
                        min="0"
                        max="1440"
                        step="1"
                        value="{{settings.Mt5CooldownMinutes}}"
                        class="input-control" />
                </label>

                <label class="enabled-row">
                    Günlük max trade:
                    <input
                        type="number"
                        id="mt5MaxTradesPerDay"
                        min="1"
                        max="100"
                        step="1"
                        value="{{settings.Mt5MaxTradesPerDay}}"
                        class="input-control" />
                </label>

                <label class="enabled-row">
                    <input type="checkbox" id="mt5DemoOnly" {{mt5DemoChecked}} />
                    Yalnız demo hesab
                </label>

                <label class="enabled-row">
                    <input type="checkbox" id="mt5OnePositionPerSymbol" {{mt5OnePositionChecked}} />
                    Eyni symbol üzrə ikinci position açma
                </label>
            </div>
        </div>

        <p class="muted">
            Qeyd: Telegram-a real Forex signal gedəndə, MT5 AutoTrade aktivdirsə, order queue-ya əlavə olunacaq.
        </p>

        <p>
            <a href="/api/mt5/status" target="_blank">
                MT5 order statusuna bax
            </a>
        </p>
    </div>

    <div class="actions">
        <button onclick="saveSettings()">Yadda saxla</button>
        <button onclick="location.reload()">Yenilə</button>
        <span id="saveResult" class="muted"></span>
    </div>

    <div class="status">
        <h2>Qısa status</h2>

        <p>
            Binary:
            <span id="binaryState">{{(settings.BinaryEnabled ? "Aktiv" : "Deaktiv")}}</span>
            /
            <strong id="binarySymbol">{{WebUtility.HtmlEncode(settings.BinaryActiveSymbol)}}</strong>
        </p>

        <p>
            Forex:
            <span id="forexState">{{(settings.ForexEnabled ? "Aktiv" : "Deaktiv")}}</span>
            /
            <strong id="forexSymbol">{{WebUtility.HtmlEncode(activeForexSymbolsText)}}</strong>
        </p>

        <p>
            MT5 AutoTrade:
            <span id="mt5State">{{(settings.Mt5AutoTradeEnabled ? "Aktiv" : "Deaktiv")}}</span>
            /
            Lot:
            <strong id="mt5LotStatus">{{mt5LotSizeValue}}</strong>
            /
            TP:
            <strong id="mt5TpStatus">{{WebUtility.HtmlEncode(settings.Mt5TakeProfitMode)}}</strong>
        </p>

        <p class="muted">
            Son yenilənmə UTC:
            <span id="updatedAt">{{settings.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss}}</span>
        </p>

        <p>
            <a id="binaryStatusLink" href="{{binaryStatusUrl}}" target="_blank">
                Binary aktiv symbol statusuna bax
            </a>
        </p>

        <p>
            <a id="forexStatusLink" href="{{forexStatusUrl}}" target="_blank">
                Forex ilk aktiv symbol statusuna bax
            </a>
        </p>
    </div>

    <div class="report-section">
        <h2>📅 Günlük nəticələr və statistika</h2>

        <div class="report-controls">
            <label>
                Tarix:
                <input type="date" id="reportDate" value="{{todayAz}}" class="input-control" />
            </label>

            <button onclick="loadReports()">Hesabatı göstər</button>

            <button class="danger-button" onclick="deleteBinaryDay()">
                Bu günün Binary nəticələrini sil
            </button>

            <button class="danger-button" onclick="deleteForexDay()">
                Bu günün Forex nəticələrini sil
            </button>
        </div>

        <div id="reportResult" class="muted">Hesabat yüklənir...</div>

        <div id="reportsContainer"></div>
    </div>

    <div class="chart-section">
        <div class="chart-section-header">
            <h2>Son Binary chart-lar</h2>
            <button class="danger-button small-button" onclick="clearCharts('binary-charts')">
                Binary chart-ları sil
            </button>
        </div>

        <p class="muted">
            Real Binary LONG/SHORT signal Telegram-a gedəndə chart burada da görünəcək.
        </p>

        <div class="chart-grid">
            {{binaryChartCards}}
        </div>
    </div>

    <div class="chart-section">
        <div class="chart-section-header">
            <h2>Son Forex chart-lar</h2>
            <button class="danger-button small-button" onclick="clearCharts('forex-charts')">
                Forex chart-ları sil
            </button>
        </div>

        <p class="muted">
            Real Forex LONG/SHORT signal Telegram-a gedəndə chart burada da görünəcək.
        </p>

        <div class="chart-grid">
            {{forexChartCards}}
        </div>
    </div>
</div>

<script>
async function saveSettings() {
    const binaryEnabledElement = document.getElementById("binaryEnabled");
    const forexEnabledElement = document.getElementById("forexEnabled");

    if (!binaryEnabledElement || !forexEnabledElement) {
        alert("Admin panel elementləri tam yüklənməyib. Səhifəni yenilə.");
        return;
    }

    const binaryEnabled = binaryEnabledElement.checked;
    const forexEnabled = forexEnabledElement.checked;

    const binaryActiveSymbols = Array
        .from(document.querySelectorAll("input[name='binaryActiveSymbols']:checked"))
        .map(x => x.value);

    const binaryActiveSymbol = binaryActiveSymbols.length > 0
        ? binaryActiveSymbols[0]
        : "";

    const forexActiveSymbols = Array
        .from(document.querySelectorAll("input[name='forexActiveSymbols']:checked"))
        .map(x => x.value);

    const forexActiveSymbol = forexActiveSymbols.length > 0
        ? forexActiveSymbols[0]
        : "";

    const mt5AutoTradeInput = document.querySelector("input[name='mt5AutoTradeEnabled']:checked");
    const mt5AutoTradeEnabled = mt5AutoTradeInput
        ? mt5AutoTradeInput.value === "true"
        : false;

    const mt5DemoOnly = document.getElementById("mt5DemoOnly").checked;

    const mt5LotSize = Number(document.getElementById("mt5LotSize").value);
    const mt5TakeProfitMode = document.querySelector("input[name='mt5TakeProfitMode']:checked").value;
    const mt5MinimumConfidence = Number(document.getElementById("mt5MinimumConfidence").value);
    const mt5MinimumGrade = document.getElementById("mt5MinimumGrade").value;
    const mt5CooldownMinutes = Number(document.getElementById("mt5CooldownMinutes").value);
    const mt5MaxTradesPerDay = Number(document.getElementById("mt5MaxTradesPerDay").value);
    const mt5OnePositionPerSymbol = document.getElementById("mt5OnePositionPerSymbol").checked;

    const forexTimeframeEl = document.querySelector('input[name="forexTimeframe"]:checked');
    const forexTimeframe = forexTimeframeEl ? forexTimeframeEl.value : "15min";

    const binaryTimeframeEl = document.querySelector('input[name="binaryTimeframe"]:checked');
    const binaryTimeframe = binaryTimeframeEl ? binaryTimeframeEl.value : "15min";

    const response = await fetch("/admin/settings", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            binaryEnabled,
            binaryActiveSymbol,
            binaryActiveSymbols,
            binaryTimeframe,
            forexEnabled,
            forexActiveSymbol,
            forexActiveSymbols,
            forexTimeframe,
            mt5AutoTradeEnabled,
            mt5LotSize,
            mt5TakeProfitMode,
            mt5MinimumConfidence,
            mt5MinimumGrade,
            mt5CooldownMinutes,
            mt5MaxTradesPerDay,
            mt5DemoOnly,
            mt5OnePositionPerSymbol
        })
    });

    const resultElement = document.getElementById("saveResult");

    if (!response.ok) {
        resultElement.innerText = "Xəta baş verdi.";
        resultElement.className = "danger";
        return;
    }

    const settings = await response.json();

    document.getElementById("binaryState").innerText = settings.binaryEnabled ? "Aktiv" : "Deaktiv";
    document.getElementById("forexState").innerText = settings.forexEnabled ? "Aktiv" : "Deaktiv";
    document.getElementById("binarySymbol").innerText = settings.binaryActiveSymbol;

    document.getElementById("forexSymbol").innerText =
        settings.forexActiveSymbols && settings.forexActiveSymbols.length > 0
            ? settings.forexActiveSymbols.join(", ")
            : settings.forexActiveSymbol;

    document.getElementById("mt5State").innerText = settings.mt5AutoTradeEnabled ? "Aktiv" : "Deaktiv";
    document.getElementById("mt5LotStatus").innerText = settings.mt5LotSize;
    document.getElementById("mt5TpStatus").innerText = settings.mt5TakeProfitMode;

    document.getElementById("updatedAt").innerText =
        settings.updatedAtUtc.replace("T", " ").substring(0, 19);

    document.getElementById("binaryStatusLink").href =
        "/api/market/status?symbol=" + encodeURIComponent(settings.binaryActiveSymbol);

    const firstForexSymbol =
        settings.forexActiveSymbols && settings.forexActiveSymbols.length > 0
            ? settings.forexActiveSymbols[0]
            : settings.forexActiveSymbol;

    document.getElementById("forexStatusLink").href =
        "/api/forex/status?symbol=" + encodeURIComponent(firstForexSymbol);

    resultElement.innerText = "Yadda saxlanıldı.";
    resultElement.className = "success";
}

async function loadReports() {
    const date = document.getElementById("reportDate").value;
    const resultElement = document.getElementById("reportResult");
    const container = document.getElementById("reportsContainer");

    resultElement.innerText = "Yüklənir...";
    resultElement.className = "muted";

    const response = await fetch("/admin/reports?date=" + encodeURIComponent(date));

    if (!response.ok) {
        resultElement.innerText = "Hesabat yüklənmədi.";
        resultElement.className = "danger";
        return;
    }

    const data = await response.json();

    container.innerHTML =
        renderReportBlock("Binary", data.binary, "binary") +
        renderReportBlock("Forex", data.forex, "forex");

    resultElement.innerText = "Hesabat yükləndi: " + data.dateAz;
    resultElement.className = "success";
}

function renderReportBlock(title, report, type) {
    return `
        <div class="section-title">${title} nəticələri</div>

        <div class="summary-grid">
            <div class="summary-card">
                <div class="label">Cəmi</div>
                <div class="value">${report.total}</div>
            </div>

            <div class="summary-card">
                <div class="label">Win</div>
                <div class="value">✅ ${report.wins}</div>
            </div>

            <div class="summary-card">
                <div class="label">Lose</div>
                <div class="value">❌ ${report.losses}</div>
            </div>

            <div class="summary-card">
                <div class="label">Pending</div>
                <div class="value">⏳ ${report.pending}</div>
            </div>

            <div class="summary-card">
                <div class="label">Win rate</div>
                <div class="value">${report.winRate}%</div>
            </div>
        </div>

        <div class="grid">
            <div>
                <h3>Symbol üzrə nəticə</h3>
                ${renderSymbolTable(report.bySymbol)}
            </div>

            <div>
                <h3>Statistik nəticə</h3>
                ${renderStats(report)}
            </div>
        </div>

        <h3>Əməliyyatlar</h3>
        ${renderTrades(report.trades, type)}
    `;
}

function renderSymbolTable(items) {
    if (!items || items.length === 0) {
        return `<p class="muted">Nəticə yoxdur.</p>`;
    }

    let rows = "";

    for (const item of items) {
        const cls = item.winRate >= 60 ? "success" : item.winRate <= 40 ? "danger" : "warning";

        rows += `
            <tr>
                <td>${escapeHtml(item.symbol)}</td>
                <td>${item.total}</td>
                <td>✅ ${item.wins}</td>
                <td>❌ ${item.losses}</td>
                <td>${item.pending}</td>
                <td class="${cls}">${item.winRate}%</td>
            </tr>
        `;
    }

    return `
        <table>
            <thead>
                <tr>
                    <th>Symbol</th>
                    <th>Total</th>
                    <th>Win</th>
                    <th>Lose</th>
                    <th>Pending</th>
                    <th>Win rate</th>
                </tr>
            </thead>
            <tbody>${rows}</tbody>
        </table>
    `;
}

function renderStats(report) {
    const best = report.bestSymbols || [];
    const worst = report.worstSymbols || [];

    return `
        <table>
            <thead>
                <tr>
                    <th>Tip</th>
                    <th>Symbol</th>
                    <th>Win rate</th>
                    <th>Total</th>
                </tr>
            </thead>
            <tbody>
                ${best.map(x => `
                    <tr>
                        <td class="success">Yaxşı</td>
                        <td>${escapeHtml(x.symbol)}</td>
                        <td>${x.winRate}%</td>
                        <td>${x.total}</td>
                    </tr>
                `).join("")}
                ${worst.map(x => `
                    <tr>
                        <td class="danger">Pis</td>
                        <td>${escapeHtml(x.symbol)}</td>
                        <td>${x.winRate}%</td>
                        <td>${x.total}</td>
                    </tr>
                `).join("")}
            </tbody>
        </table>
    `;
}

function renderTrades(trades, type) {
    if (!trades || trades.length === 0) {
        return `<p class="muted">Bu tarixdə əməliyyat yoxdur.</p>`;
    }

    let rows = "";

    for (const trade of trades) {
        const resultClass =
            trade.result === "WIN" || trade.result === "WIN_TP2"
                ? "success"
                : trade.result === "LOSS"
                    ? "danger"
                    : "warning";

        rows += `
            <tr>
                <td>${escapeHtml(trade.createdAtAz)}</td>
                <td>${escapeHtml(trade.symbol)}</td>
                <td>${escapeHtml(trade.direction)}</td>
                <td class="${resultClass}">${escapeHtml(trade.result)}</td>
                <td>${trade.confidence ?? ""}</td>
                <td>${trade.entryPrice}</td>
                <td>${trade.exitPrice ?? "-"}</td>
                <td>
                    <button class="danger-button small-button" onclick="deleteTrade('${type}', '${trade.id}')">
                        Sil
                    </button>
                </td>
            </tr>
        `;
    }

    return `
        <table>
            <thead>
                <tr>
                    <th>Vaxt AZT</th>
                    <th>Symbol</th>
                    <th>Direction</th>
                    <th>Result</th>
                    <th>Conf.</th>
                    <th>Entry</th>
                    <th>Exit</th>
                    <th>Sil</th>
                </tr>
            </thead>
            <tbody>${rows}</tbody>
        </table>
    `;
}

async function deleteTrade(type, id) {
    if (!confirm("Bu nəticə silinsin?")) {
        return;
    }

    const response = await fetch(`/admin/reports/${type}/delete/${id}`, {
        method: "POST"
    });

    if (!response.ok) {
        alert("Silinmədi.");
        return;
    }

    await loadReports();
}

async function deleteBinaryDay() {
    const date = document.getElementById("reportDate").value;

    if (!confirm(date + " tarixinin bütün Binary nəticələri silinsin?")) {
        return;
    }

    const response = await fetch("/admin/reports/binary/delete-day", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({ date })
    });

    if (!response.ok) {
        alert("Binary nəticələr silinmədi.");
        return;
    }

    await loadReports();
}

async function deleteForexDay() {
    const date = document.getElementById("reportDate").value;

    if (!confirm(date + " tarixinin bütün Forex nəticələri silinsin?")) {
        return;
    }

    const response = await fetch("/admin/reports/forex/delete-day", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({ date })
    });

    if (!response.ok) {
        alert("Forex nəticələr silinmədi.");
        return;
    }

    await loadReports();
}

async function deleteChart(button) {
    const folderName = button.dataset.folder;
    const fileName = button.dataset.file;

    if (!confirm("Bu chart silinsin?")) {
        return;
    }

    const response = await fetch("/admin/charts/delete", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            folderName,
            fileName
        })
    });

    if (!response.ok) {
        alert("Chart silinmedi.");
        return;
    }

    location.reload();
}

async function clearCharts(folderName) {
    const text = folderName === "binary-charts"
        ? "Bütün Binary chart-lar silinsin?"
        : "Bütün Forex chart-lar silinsin?";

    if (!confirm(text)) {
        return;
    }

    const response = await fetch("/admin/charts/clear", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            folderName
        })
    });

    if (!response.ok) {
        alert("Chart-lar silinmedi.");
        return;
    }

    location.reload();
}

function escapeHtml(value) {
    if (value === null || value === undefined) {
        return "";
    }

    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}

loadReports();
</script>
</body>
</html>
""";
    }

    private static object BuildBinaryReport(List<BinaryTradeResultEntity> trades)
    {
        var total = trades.Count;
        var wins = trades.Count(x => x.Result == "WIN");
        var losses = trades.Count(x => x.Result == "LOSS");
        var draws = trades.Count(x => x.Result == "DRAW");
        var pending = trades.Count(x => x.Result == "PENDING");

        var completed = wins + losses;

        var winRate = completed > 0
            ? Math.Round((decimal)wins / completed * 100m, 1)
            : 0;

        var bySymbol = BuildBinarySymbolStats(trades);

        return new
        {
            total,
            wins,
            losses,
            draws,
            pending,
            winRate,
            bySymbol,
            bestSymbols = bySymbol
                .Where(x => x.Total >= 2)
                .OrderByDescending(x => x.WinRate)
                .ThenByDescending(x => x.Total)
                .Take(5)
                .ToList(),
            worstSymbols = bySymbol
                .Where(x => x.Total >= 2)
                .OrderBy(x => x.WinRate)
                .ThenByDescending(x => x.Total)
                .Take(5)
                .ToList(),
            trades = trades.Select(x => new
            {
                id = x.Id,
                createdAtAz = ToAzerbaijanTime(x.CreatedAtUtc).ToString("HH:mm:ss"),
                x.Symbol,
                x.Direction,
                x.Result,
                x.Confidence,
                x.EntryPrice,
                x.ExitPrice
            }).ToList()
        };
    }

    private static object BuildForexReport(List<ForexTradeResultEntity> trades)
    {
        var total = trades.Count;

        var wins = trades.Count(IsForexWin);
        var losses = trades.Count(IsForexLoss);
        var pending = trades.Count(x => x.Result == "PENDING");
        var expired = trades.Count(x => x.Result == "EXPIRED");
        var ambiguous = trades.Count(x => x.Result == "AMBIGUOUS");

        var completed = wins + losses;

        var winRate = completed > 0
            ? Math.Round((decimal)wins / completed * 100m, 1)
            : 0;

        var bySymbol = BuildForexSymbolStats(trades);

        return new
        {
            total,
            wins,
            losses,
            draws = 0,
            pending,
            expired,
            ambiguous,
            winRate,
            bySymbol,
            bestSymbols = bySymbol
                .Where(x => x.Total >= 2)
                .OrderByDescending(x => x.WinRate)
                .ThenByDescending(x => x.Total)
                .Take(5)
                .ToList(),
            worstSymbols = bySymbol
                .Where(x => x.Total >= 2)
                .OrderBy(x => x.WinRate)
                .ThenByDescending(x => x.Total)
                .Take(5)
                .ToList(),
            trades = trades.Select(x => new
            {
                id = x.Id,
                createdAtAz = ToAzerbaijanTime(x.CreatedAtUtc).ToString("HH:mm:ss"),
                x.Symbol,
                x.Direction,
                x.Result,
                confidence = (int?)null,
                x.EntryPrice,
                x.ExitPrice
            }).ToList()
        };
    }

    private static List<SymbolStat> BuildBinarySymbolStats(List<BinaryTradeResultEntity> trades)
    {
        return trades
            .GroupBy(x => x.Symbol)
            .Select(group =>
            {
                var wins = group.Count(x => x.Result == "WIN");
                var losses = group.Count(x => x.Result == "LOSS");
                var pending = group.Count(x => x.Result == "PENDING");
                var completed = wins + losses;

                return new SymbolStat
                {
                    Symbol = group.Key,
                    Total = group.Count(),
                    Wins = wins,
                    Losses = losses,
                    Pending = pending,
                    WinRate = completed > 0
                        ? Math.Round((decimal)wins / completed * 100m, 1)
                        : 0
                };
            })
            .OrderByDescending(x => x.Total)
            .ToList();
    }

    private static List<SymbolStat> BuildForexSymbolStats(List<ForexTradeResultEntity> trades)
    {
        return trades
            .GroupBy(x => x.Symbol)
            .Select(group =>
            {
                var wins = group.Count(IsForexWin);
                var losses = group.Count(IsForexLoss);
                var pending = group.Count(x => x.Result == "PENDING");
                var completed = wins + losses;

                return new SymbolStat
                {
                    Symbol = group.Key,
                    Total = group.Count(),
                    Wins = wins,
                    Losses = losses,
                    Pending = pending,
                    WinRate = completed > 0
                        ? Math.Round((decimal)wins / completed * 100m, 1)
                        : 0
                };
            })
            .OrderByDescending(x => x.Total)
            .ToList();
    }

    private static bool IsForexWin(ForexTradeResultEntity trade)
    {
        return trade.Result == "WIN" ||
               trade.Result == "WIN_TP2" ||
               trade.IsTp1Hit ||
               trade.IsTp2Hit;
    }

    private static bool IsForexLoss(ForexTradeResultEntity trade)
    {
        return (trade.Result == "LOSS" || trade.IsStopLossHit) &&
               trade.Result != "WIN" &&
               trade.Result != "WIN_TP2" &&
               !trade.IsTp1Hit &&
               !trade.IsTp2Hit;
    }

    private string BuildChartCards(
        string folderName,
        string emptyTitle,
        string emptyDescription)
    {
        var charts = GetLatestChartFiles(folderName);

        if (charts.Count == 0)
        {
            return $$"""
<div class="chart-card">
    <div class="chart-title">{{WebUtility.HtmlEncode(emptyTitle)}}</div>
    <p class="muted">{{WebUtility.HtmlEncode(emptyDescription)}}</p>
</div>
""";
        }

        var sb = new StringBuilder();

        foreach (var chart in charts)
        {
            var title = WebUtility.HtmlEncode(chart.FileName);
            var url = WebUtility.HtmlEncode(chart.Url);
            var folder = WebUtility.HtmlEncode(folderName);
            var file = WebUtility.HtmlEncode(chart.FileName);

            sb.AppendLine($$"""
<div class="chart-card">
    <div class="chart-title">{{title}}</div>

    <a href="{{url}}" target="_blank">
        <img src="{{url}}" alt="{{title}}" />
    </a>

    <div class="chart-card-footer">
        <span class="chart-date">{{chart.CreatedAtUtc:MM-dd HH:mm}} UTC</span>
        <button
            class="danger-button small-button"
            data-folder="{{folder}}"
            data-file="{{file}}"
            onclick="deleteChart(this)">
            Sil
        </button>
    </div>
</div>
""");
        }

        return sb.ToString();
    }

    private List<ChartFileInfo> GetLatestChartFiles(string folderName)
    {
        var chartDirectory = GetChartDirectory(folderName);

        if (!Directory.Exists(chartDirectory))
            return new List<ChartFileInfo>();

        return Directory
            .GetFiles(chartDirectory, "*.png")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Take(6)
            .Select(file => new ChartFileInfo
            {
                FileName = file.Name,
                Url = "/" + folderName + "/" + Uri.EscapeDataString(file.Name),
                CreatedAtUtc = file.CreationTimeUtc
            })
            .ToList();
    }

    private (bool Success, string Message) TryDeleteChart(
        string folderName,
        string fileName)
    {
        if (!IsAllowedChartFolder(folderName))
            return (false, "Chart folder icaze verilmir.");

        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "Fayl adi bosdur.");

        var safeFileName = Path.GetFileName(fileName);

        if (!safeFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return (false, "Yalniz PNG chart siline biler.");

        var chartDirectory = GetChartDirectory(folderName);

        if (!Directory.Exists(chartDirectory))
            return (false, "Chart folder tapilmadi.");

        var fullPath = Path.GetFullPath(
            Path.Combine(chartDirectory, safeFileName));

        var allowedRoot = Path.GetFullPath(chartDirectory);

        if (!fullPath.StartsWith(
                allowedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Fayl yolu icaze verilmir.");
        }

        if (!System.IO.File.Exists(fullPath))
            return (false, "Chart fayli tapilmadi.");

        try
        {
            System.IO.File.Delete(fullPath);
            return (true, "Chart silindi.");
        }
        catch (Exception ex)
        {
            return (false, $"Chart silinmedi: {ex.Message}");
        }
    }

    private string GetChartDirectory(string folderName)
    {
        var wwwroot = _environment.WebRootPath;

        if (string.IsNullOrWhiteSpace(wwwroot))
        {
            wwwroot = Path.Combine(
                _environment.ContentRootPath,
                "wwwroot");
        }

        return Path.Combine(
            wwwroot,
            folderName);
    }

    private static bool IsAllowedChartFolder(string folderName)
    {
        return folderName == "binary-charts" ||
               folderName == "forex-charts";
    }

    private static string BuildRadioButtons(
        string name,
        List<string> symbols,
        string activeSymbol)
    {
        var sb = new StringBuilder();

        foreach (var symbol in symbols)
        {
            var encodedSymbol = WebUtility.HtmlEncode(symbol);
            var selected = symbol == activeSymbol ? "checked" : "";

            sb.AppendLine($$"""
<label class="symbol-option">
    <input type="radio" name="{{name}}" value="{{encodedSymbol}}" {{selected}} />
    {{encodedSymbol}}
</label>
""");
        }

        return sb.ToString();
    }

    private static string BuildCheckboxes(
        string name,
        List<string> symbols,
        List<string> activeSymbols)
    {
        var sb = new StringBuilder();

        activeSymbols ??= new List<string>();

        foreach (var symbol in symbols)
        {
            var encodedSymbol = WebUtility.HtmlEncode(symbol);

            var selected = activeSymbols.Contains(symbol)
                ? "checked"
                : "";

            sb.AppendLine($$"""
<label class="symbol-option">
    <input type="checkbox" name="{{name}}" value="{{encodedSymbol}}" {{selected}} />
    {{encodedSymbol}}
</label>
""");
        }

        return sb.ToString();
    }

    private static DateTime ParseAzerbaijanDateOrToday(string? value)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return date.Date;
        }

        return ToAzerbaijanTime(DateTime.UtcNow).Date;
    }

    private static (DateTime StartUtc, DateTime EndUtc) GetUtcRangeForAzerbaijanDate(DateTime dateAz)
    {
        var timeZone = GetAzerbaijanTimeZone();

        var startAz = dateAz.Date;
        var endAz = startAz.AddDays(1);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startAz, timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endAz, timeZone);

        return (startUtc, endUtc);
    }

    private static DateTime ToAzerbaijanTime(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        return TimeZoneInfo.ConvertTimeFromUtc(
            utc,
            GetAzerbaijanTimeZone());
    }

    private static TimeZoneInfo GetAzerbaijanTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Azerbaijan Standard Time");
        }
        catch
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku");
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    "Azerbaijan Time",
                    TimeSpan.FromHours(4),
                    "Azerbaijan Time",
                    "Azerbaijan Time");
            }
        }
    }

    public sealed class AdminChartDeleteRequest
    {
        public string FolderName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
    }

    public sealed class AdminChartClearRequest
    {
        public string FolderName { get; set; } = string.Empty;
    }

    public sealed class ReportDateRequest
    {
        public string Date { get; set; } = string.Empty;
    }

    private sealed class ChartFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }

    private sealed class SymbolStat
    {
        public string Symbol { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Pending { get; set; }
        public decimal WinRate { get; set; }
    }
}