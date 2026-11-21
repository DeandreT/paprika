using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace paprika;

public sealed record OperationsIdentity(string Name, string[] Roles, string[] Workflows)
{
    public bool Administrator => Roles.Contains("administrator", StringComparer.Ordinal);

    public bool CanRead(string workflow) =>
        Administrator
        || (
            Roles.Any(r => r is "reviewer" or "viewer")
            && Workflows.Contains(workflow, StringComparer.Ordinal)
        );

    public bool CanReview(string workflow) =>
        CanRead(workflow) && (Administrator || Roles.Contains("reviewer", StringComparer.Ordinal));
}

public sealed class OperationsAccess(
    IConfiguration configuration,
    IDataProtectionProvider protection
)
{
    private const string CookieName = "paprikaOperations";
    private readonly IDataProtector protector = protection.CreateProtector(
        "paprika.Operations.Session.v1"
    );
    public bool Shared =>
        string.Equals(
            configuration["paprika:Operations:Mode"],
            "shared",
            StringComparison.OrdinalIgnoreCase
        );

    public OperationsIdentity? Resolve(HttpContext context)
    {
        if (!Shared)
            return new("local", ["administrator"], ["*"]);
        // Identity names alone never confer privileges. All roles and workflows come from
        // the server-side allowlist, including identities established by ASP.NET auth.
        string? name = null;
        if (
            configuration.GetValue<bool>("paprika:Operations:UseAspNetIdentity")
            && context.User.Identity?.IsAuthenticated == true
        )
            name = context.User.Identity.Name
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (
            name is null
            && context.Request.Headers.Authorization.Count == 0
            && context.Request.Cookies.TryGetValue(CookieName, out var cookie)
        )
        {
            try
            {
                var session = System.Text.Json.JsonSerializer.Deserialize<OperationsSessionTicket>(
                    protector.Unprotect(cookie)
                );
                if (session?.ExpiresAt > DateTimeOffset.UtcNow)
                    name = session.Name;
            }
            catch (Exception ex)
                when (ex is CryptographicException or System.Text.Json.JsonException) { }
        }
        if (name is null)
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]));
                foreach (
                    var account in configuration
                        .GetSection("paprika:Operations:Users")
                        .GetChildren()
                )
                {
                    var secret = account["Token"];
                    if (
                        !string.IsNullOrEmpty(secret)
                        && secret.Length >= 32
                        && CryptographicOperations.FixedTimeEquals(
                            supplied,
                            SHA256.HashData(Encoding.UTF8.GetBytes(secret))
                        )
                    )
                    {
                        name = account["Name"];
                        break;
                    }
                }
            }
        }
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var matches = configuration
            .GetSection("paprika:Operations:Users")
            .GetChildren()
            .Where(u => string.Equals(u["Name"], name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            return null;
        var user = matches[0];
        return new(
            name,
            user.GetSection("Roles").Get<string[]>() ?? [],
            user.GetSection("Workflows").Get<string[]>() ?? []
        );
    }

    public static OperationsIdentity Identity(HttpContext context) =>
        (OperationsIdentity)context.Items[typeof(OperationsIdentity)]!;

    public static string Actor(HttpContext context) => Identity(context).Name;

    public void SignIn(HttpContext context, OperationsIdentity identity)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(8);
        context.Response.Cookies.Append(
            CookieName,
            protector.Protect(
                System.Text.Json.JsonSerializer.Serialize(
                    new OperationsSessionTicket(identity.Name, expires)
                )
            ),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/api",
                Expires = expires,
                IsEssential = true,
            }
        );
    }

    public void SignOut(HttpContext context) =>
        context.Response.Cookies.Delete(
            CookieName,
            new CookieOptions { Path = "/api", SameSite = SameSiteMode.Strict }
        );
}

internal sealed record OperationsSessionTicket(string Name, DateTimeOffset ExpiresAt);

public static class OperationsPlatformRegistration
{
    /// <summary>Use only for handlers that enforce stored-resource workflow, role and archive checks themselves.</summary>
    public static RouteHandlerBuilder WithOperationsScopedAccess(
        this RouteHandlerBuilder builder
    ) => builder.WithMetadata(new OperationsScopedPermission());

    public static RouteHandlerBuilder WithOperationsPacketAccess(
        this RouteHandlerBuilder builder,
        string parameterName = "packetId",
        bool requireClaim = false
    ) => builder.WithMetadata(new OperationsPacketPermission(parameterName, requireClaim));

    public static IServiceCollection AddOperationsPlatform(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddSingleton<OperationsAccess>();
        services.AddSingleton<OperationsPlatformStore>();
        services.AddDataProtection();
        return services;
    }

    public static IApplicationBuilder UseOperationsPlatform(this IApplicationBuilder app) =>
        app.Use(
            async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    await next();
                    return;
                }
                var access = context.RequestServices.GetRequiredService<OperationsAccess>();
                var identity = access.Resolve(context);
                if (
                    context.Request.Path == "/api/operations-platform/session"
                    && HttpMethods.IsGet(context.Request.Method)
                )
                {
                    await context.Response.WriteAsJsonAsync(
                        new OperationsSessionResponse(
                            access.Shared ? "shared" : "local",
                            identity is not null,
                            identity
                        )
                    );
                    return;
                }
                if (identity is null)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(
                        new ApiError("Authentication required.")
                    );
                    return;
                }
                context.Items[typeof(OperationsIdentity)] = identity;
                var store = context.RequestServices.GetRequiredService<Store>();
                var operations =
                    context.RequestServices.GetRequiredService<OperationsPlatformStore>();
                var parts = context.Request.Path.Value!.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries
                );
                var read =
                    HttpMethods.IsGet(context.Request.Method)
                    || HttpMethods.IsHead(context.Request.Method);
                if (access.Shared && read && parts.Length == 2 && parts[1] == "config")
                {
                    await next();
                    return;
                }
                if (
                    access.Shared
                    && !identity.Administrator
                    && read
                    && parts.Length == 2
                    && parts[1] == "workflows"
                )
                {
                    var catalog = store.Workflows.Catalog();
                    var workflows = catalog
                        .Entries.Where(e =>
                            e.Kind == "workflow"
                            && e.Status == "published"
                            && identity.CanRead(e.Id)
                        )
                        .ToArray();
                    var releaseRefs = workflows
                        .Where(e => e.Workflow is not null)
                        .Select(e => e.Workflow!.Release)
                        .ToHashSet();
                    var releases = catalog
                        .Entries.Where(e =>
                            e.Kind == "release" && releaseRefs.Contains(new(e.Id, e.Version))
                        )
                        .ToArray();
                    var typeRefs = releases
                        .Where(e => e.Release is not null)
                        .SelectMany(e => e.Release!.Types)
                        .ToHashSet();
                    var types = catalog
                        .Entries.Where(e =>
                            e.Kind == "type" && typeRefs.Contains(new(e.Id, e.Version))
                        )
                        .ToArray();
                    var defaultRef =
                        workflows.Any(e =>
                            e.Id == catalog.DefaultWorkflow.Id
                            && e.Version == catalog.DefaultWorkflow.Version
                        )
                            ? catalog.DefaultWorkflow
                        : workflows.FirstOrDefault() is { } first
                            ? new VersionRef(first.Id, first.Version)
                        : new VersionRef("", 0);
                    await context.Response.WriteAsJsonAsync(
                        catalog with
                        {
                            Entries = [.. workflows, .. releases, .. types],
                            DefaultWorkflow = defaultRef,
                            Stats = catalog.Stats.Where(s => identity.CanRead(s.Id)).ToArray(),
                            ProviderProfiles = [],
                            CanUpload = false,
                            ClassifierProviders = ReadableClassifiers(
                                workflows,
                                catalog.ClassifierProviders
                            ),
                            LiveModel = "",
                        }
                    );
                    return;
                }
                if (
                    context.GetEndpoint()?.Metadata.GetMetadata<OperationsPacketPermission>() is
                    { } permission
                )
                {
                    var routeId = context.Request.RouteValues[permission.ParameterName]?.ToString();
                    var queryIds = context.Request.Query[permission.ParameterName];
                    var ambiguous =
                        queryIds.Count > 1
                        || routeId is not null && queryIds.Count == 1 && routeId != queryIds[0];
                    var packetId = ambiguous ? null : routeId ?? queryIds.FirstOrDefault();
                    var target = packetId is null ? null : store.Get(packetId);
                    if (
                        target is null
                        || operations.IsArchived(target.Id)
                        || !(
                            read
                                ? identity.CanRead(target.EffectiveWorkflow.Id)
                                : identity.CanReview(target.EffectiveWorkflow.Id)
                        )
                    )
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsJsonAsync(new ApiError("Packet not found."));
                        return;
                    }
                    if (access.Shared && !read && permission.RequireClaim)
                    {
                        operations.Tasks(identity);
                        using var connection = store.Open();
                        using var transaction = connection.BeginTransaction(deferred: false);
                        if (
                            operations.ValidateMutation(
                                connection,
                                transaction,
                                target,
                                identity.Name
                            ) is
                            { } error
                        )
                        {
                            context.Response.StatusCode = 409;
                            await context.Response.WriteAsJsonAsync(new ApiError(error));
                            return;
                        }
                    }
                    await next();
                    return;
                }
                // Never trust a workflow supplied by the caller for an existing packet.
                if (parts.Length >= 3 && parts[1] == "packets" && store.Get(parts[2]) is { } packet)
                {
                    if (
                        operations.IsArchived(packet.Id)
                        || !(
                            read
                                ? identity.CanRead(packet.EffectiveWorkflow.Id)
                                : identity.CanReview(packet.EffectiveWorkflow.Id)
                        )
                    )
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsJsonAsync(new ApiError("Packet not found."));
                        return;
                    }
                    await next();
                    return;
                }
                if (parts.Length >= 3 && parts[1] == "packets" && parts[2] != "demo")
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(new ApiError("Packet not found."));
                    return;
                }
                if (parts.Length == 2 && parts[1] == "packets" && read)
                {
                    await context.Response.WriteAsJsonAsync(
                        store
                            .All()
                            .Where(p =>
                                identity.CanRead(p.EffectiveWorkflow.Id)
                                && !operations.IsArchived(p.Id)
                            )
                            .Select(PacketResponse.From)
                    );
                    return;
                }
                if (parts.Length >= 2 && parts[1] == "operations-platform")
                {
                    await next(); // Every new operation performs its own role/workflow check.
                    return;
                }
                if (
                    context.GetEndpoint()?.Metadata.GetMetadata<OperationsScopedPermission>()
                    is not null
                )
                {
                    await next();
                    return;
                }
                if (!access.Shared)
                {
                    await next();
                    return;
                }
                // Global catalogs, uploads, messaging, deliveries, metrics and future API groups
                // are admin-only by default. This explicit deny prevents legacy endpoint bypasses.
                if (!identity.Administrator)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(
                        new ApiError("Administrator access required for this operation.")
                    );
                    return;
                }
                await next();
            }
        );

    private static ClassifierProviderInfo[] ReadableClassifiers(
        CatalogEntry[] workflows,
        ClassifierProviderInfo[] providers
    )
    {
        var selected = new HashSet<(string Provider, string Model)>();
        foreach (var entry in workflows)
        {
            if (entry.Workflow is not { Mode: "live" } live)
                continue;
            var provider = live.ProviderProfile is null or "default"
                ? entry.ResolvedClassifierProvider
                : live.ProviderProfile;
            if (provider is not null)
                selected.Add((provider, live.Model));
        }
        return providers.Where(p => selected.Contains((p.Id, p.Model))).ToArray();
    }
}

public sealed record OperationsPacketPermission(
    string ParameterName = "packetId",
    bool RequireClaim = false
);

public sealed record OperationsScopedPermission;
