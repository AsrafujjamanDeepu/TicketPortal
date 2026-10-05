namespace TicketPortal.Api.Services;

/// <summary>A validated business-rule rejection with a safe message for the caller.</summary>
public sealed class BusinessRuleException(string message, int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode is >= 400 and < 500
        ? statusCode
        : StatusCodes.Status400BadRequest;
}
