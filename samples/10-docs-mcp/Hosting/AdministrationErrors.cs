using DocsMcpSample.Search;
using Microsoft.AspNetCore.Diagnostics;

namespace DocsMcpSample.Hosting;

internal sealed class AdministrationErrors(Settings settings, ILogger<AdministrationErrors> logger) : IExceptionHandler
{
	public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
	{
		if (context.Connection.LocalPort != settings.AdminPort) { return false; }
		var status = exception switch
		{
			IndexUnavailableException => StatusCodes.Status503ServiceUnavailable,
			ArgumentException => StatusCodes.Status400BadRequest,
			KeyNotFoundException => StatusCodes.Status404NotFound,
			_ => StatusCodes.Status500InternalServerError,
		};
		if (status == 500) { Log.HttpFailure(logger, exception); }
		await Microsoft.AspNetCore.Http.Results.Problem(
			detail: status == 500 ? "An unexpected error occurred. Check the server logs for details." : exception.Message,
			statusCode: status).ExecuteAsync(context);
		return true;
	}
}
