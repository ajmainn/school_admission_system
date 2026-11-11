using Microsoft.AspNetCore.Mvc;
using SchoolAdmission.Api.Services;

namespace SchoolAdmission.Api.Controllers;

[ApiController]
[TypeFilter(typeof(AdmissionExceptionFilter))]
[Route("me")]
public sealed class MeController(IAdmissionService admissions) : ControllerBase
{
    [HttpGet("holds")]
    public async Task<IActionResult> MyHolds([FromHeader(Name = "X-Student-Id")] string? studentId, CancellationToken ct)
    {
        return Ok(await admissions.GetMyHoldsAsync(studentId ?? string.Empty, ct));
    }
}
