using Microsoft.AspNetCore.Mvc;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Services;

namespace SchoolAdmission.Api.Controllers;

[ApiController]
[TypeFilter(typeof(AdmissionExceptionFilter))]
[Route("holds")]
public sealed class HoldsController(IAdmissionService admissions) : ControllerBase
{
    [HttpGet("{holdId}")]
    public async Task<IActionResult> Details(string holdId, CancellationToken ct)
    {
        var hold = await admissions.GetHoldAsync(holdId, ct);
        return hold is null ? NotFound(new ErrorResponse("Hold not found.")) : Ok(hold);
    }

    [HttpPost("{holdId}/confirm")]
    public async Task<IActionResult> Confirm(string holdId,
        [FromHeader(Name = "X-Student-Id")] string? studentId, CancellationToken ct)
    {
        return Ok(await admissions.ConfirmAsync(holdId, studentId ?? string.Empty, ct));
    }

    [HttpDelete("{holdId}")]
    public async Task<IActionResult> Cancel(string holdId,
        [FromHeader(Name = "X-Student-Id")] string? studentId, CancellationToken ct)
    {
        await admissions.CancelAsync(holdId, studentId ?? string.Empty, ct);
        return NoContent();
    }
}
