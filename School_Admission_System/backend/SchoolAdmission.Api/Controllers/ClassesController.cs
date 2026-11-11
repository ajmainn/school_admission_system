using Microsoft.AspNetCore.Mvc;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Services;

namespace SchoolAdmission.Api.Controllers;

[ApiController]
[Route("classes")]
public sealed class ClassesController(IAdmissionService admissions) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClassRequest request, CancellationToken ct)
    {
        try
        {
            var result = await admissions.CreateClassAsync(request, ct);
            return CreatedAtAction(nameof(Details), new { classId = result.Id }, result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ErrorResponse(exception.Message));
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        return Ok(await admissions.GetClassesAsync(ct));
    }

    [HttpGet("{classId}")]
    public async Task<IActionResult> Details(string classId, CancellationToken ct)
    {
        if (!classId.StartsWith("c_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(classId[2..], "N", out _))
            return BadRequest(new ErrorResponse("Class ID must be c_ followed by a 32-digit GUID."));

        var result = await admissions.GetClassAsync(classId, ct);
        return result is null
            ? NotFound(new ErrorResponse("Class not found."))
            : Ok(result);
    }

    [HttpPost("{classId}/holds")]
    public async Task<IActionResult> Hold(string classId,
        [FromHeader(Name = "X-Student-Id")] string? studentId, CancellationToken ct)
    {
        try
        {
            var result = await admissions.CreateHoldAsync(classId, studentId ?? string.Empty, ct);
            return StatusCode(201, result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ErrorResponse(exception.Message));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new ErrorResponse(exception.Message));
        }
        catch (AdmissionConflictException exception)
        {
            return Conflict(new ErrorResponse(exception.Message));
        }
    }
}
