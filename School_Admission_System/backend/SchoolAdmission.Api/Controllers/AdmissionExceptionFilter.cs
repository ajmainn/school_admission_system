using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Services;

namespace SchoolAdmission.Api.Controllers;

public sealed class AdmissionExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var status = context.Exception switch
        {
            ArgumentException => 400,
            KeyNotFoundException => 404,
            AdmissionConflictException => 409,
            _ => (int?)null
        };
        if (status is null) return;
        context.Result = new ObjectResult(new ErrorResponse(context.Exception.Message)) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
