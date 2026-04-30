using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using PocketSignal.Api.Models.Admin;
using PocketSignal.Api.Services.Admin;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly IAdminRuntimeSettingsService _settingsService;
    private readonly IWebHostEnvironment _environment;

    public AdminController(
        IAdminRuntimeSettingsService settingsService,
        IWebHostEnvironment environment)
    {
        _settingsService = settingsService;
        _environment = environment;
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

    private string BuildHtml(AdminRuntimeSettings settings)
    {
        var binaryRadios = BuildRadioButtons(
            "binaryActiveSymbol",
            settings.BinarySymbols,
            settings.BinaryActiveSymbol);

        var forexRadios = BuildRadioButtons(
            "forexActiveSymbol",
            settings.ForexSymbols,
            settings.ForexActiveSymbol);

        var binaryChecked = settings.BinaryEnabled ? "checked" : "";
        var forexChecked = settings.ForexEnabled ? "checked" : "";

        var binaryStatusUrl =
            $"/api/market/status?symbol={WebUtility.UrlEncode(settings.BinaryActiveSymbol)}";

        var forexStatusUrl =
            $"/api/forex/status?symbol={WebUtility.UrlEncode(settings.ForexActiveSymbol)}";

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
            max-width: 1200px;
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

        .card {
            background: #181f26;
            border: 1px solid #2b3540;
            border-radius: 16px;
            padding: 20px;
            box-shadow: 0 8px 24px rgba(0,0,0,0.25);
        }

        .card h2 {
            margin-top: 0;
            font-size: 22px;
        }

        .enabled-row {
            display: flex;
            align-items: center;
            gap: 10px;
            margin-bottom: 18px;
            font-size: 17px;
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

        .status {
            margin-top: 20px;
            background: #11171d;
            border: 1px solid #2b3540;
            border-radius: 16px;
            padding: 18px;
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

        .chart-section {
            margin-top: 20px;
            background: #11171d;
            border: 1px solid #2b3540;
            border-radius: 16px;
            padding: 18px;
        }

        .chart-grid {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 18px;
        }

        .chart-card {
            background: #181f26;
            border: 1px solid #2b3540;
            border-radius: 16px;
            padding: 12px;
        }

        .chart-card img {
            width: 100%;
            border-radius: 12px;
            border: 1px solid #2b3540;
            display: block;
        }

        .chart-title {
            font-size: 14px;
            color: #d4d4d8;
            margin-bottom: 8px;
            word-break: break-all;
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

            <div class="symbols">
                {{binaryRadios}}
            </div>
        </div>

        <div class="card">
            <h2>Forex Bot</h2>

            <label class="enabled-row">
                <input type="checkbox" id="forexEnabled" {{forexChecked}} />
                Forex aktivdir
            </label>

            <div class="symbols">
                {{forexRadios}}
            </div>
        </div>
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
            <strong id="forexSymbol">{{WebUtility.HtmlEncode(settings.ForexActiveSymbol)}}</strong>
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
                Forex aktiv symbol statusuna bax
            </a>
        </p>
    </div>

    <div class="chart-section">
        <h2>Son Binary chart-lar</h2>
        <p class="muted">
            Real Binary LONG/SHORT signal Telegram-a gedəndə chart burada da görünəcək.
        </p>

        <div class="chart-grid">
            {{binaryChartCards}}
        </div>
    </div>

    <div class="chart-section">
        <h2>Son Forex chart-lar</h2>
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
    const binaryEnabled = document.getElementById("binaryEnabled").checked;
    const forexEnabled = document.getElementById("forexEnabled").checked;

    const binaryActiveSymbol = document.querySelector("input[name='binaryActiveSymbol']:checked").value;
    const forexActiveSymbol = document.querySelector("input[name='forexActiveSymbol']:checked").value;

    const response = await fetch("/admin/settings", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            binaryEnabled,
            binaryActiveSymbol,
            forexEnabled,
            forexActiveSymbol
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
    document.getElementById("forexSymbol").innerText = settings.forexActiveSymbol;
    document.getElementById("updatedAt").innerText = settings.updatedAtUtc.replace("T", " ").substring(0, 19);

    document.getElementById("binaryStatusLink").href =
        "/api/market/status?symbol=" + encodeURIComponent(settings.binaryActiveSymbol);

    document.getElementById("forexStatusLink").href =
        "/api/forex/status?symbol=" + encodeURIComponent(settings.forexActiveSymbol);

    resultElement.innerText = "Yadda saxlanıldı.";
    resultElement.className = "success";
}
</script>
</body>
</html>
""";
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

            sb.AppendLine($$"""
<div class="chart-card">
    <div class="chart-title">{{title}}</div>
    <a href="{{url}}" target="_blank">
        <img src="{{url}}" alt="{{title}}" />
    </a>
</div>
""");
        }

        return sb.ToString();
    }

    private List<ChartFileInfo> GetLatestChartFiles(string folderName)
    {
        var wwwroot = _environment.WebRootPath;

        if (string.IsNullOrWhiteSpace(wwwroot))
        {
            wwwroot = Path.Combine(
                _environment.ContentRootPath,
                "wwwroot");
        }

        var chartDirectory = Path.Combine(
            wwwroot,
            folderName);

        if (!Directory.Exists(chartDirectory))
            return new List<ChartFileInfo>();

        return Directory
            .GetFiles(chartDirectory, "*.png")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Take(12)
            .Select(file => new ChartFileInfo
            {
                FileName = file.Name,
                Url = "/" + folderName + "/" + Uri.EscapeDataString(file.Name),
                CreatedAtUtc = file.CreationTimeUtc
            })
            .ToList();
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

    private sealed class ChartFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}