namespace LdmsOutletCameraHelper;

// Chrome/Edge block a request from a public HTTPS page to http://localhost unless the
// preflight response carries Access-Control-Allow-Private-Network: true. ASP.NET Core
// CORS does not emit that header, so add it here before the CORS middleware runs.
public static class PrivateNetworkAccessMiddleware
{
    public static IApplicationBuilder UsePrivateNetworkAccess(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            bool isPreflight = HttpMethods.IsOptions(context.Request.Method) &&
                context.Request.Headers.TryGetValue("Access-Control-Request-Private-Network", out var requested) &&
                requested == "true";

            if (isPreflight)
            {
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }

            await next();
        });
    }
}
