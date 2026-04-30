using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DbController : ControllerBase
{
    private readonly PocketSignalDbContext _dbContext;

    public DbController(PocketSignalDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        var canConnect = await _dbContext.Database.CanConnectAsync();

        return Ok(new
        {
            database = "PocketSignalDb",
            canConnect
        });
    }
}