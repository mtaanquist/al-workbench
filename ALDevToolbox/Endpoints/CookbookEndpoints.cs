using System.IO.Compression;
using System.Text;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Cookbook;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

internal static class CookbookEndpoints
{
    public static IEndpointRouteBuilder MapCookbookEndpoints(this IEndpointRouteBuilder app)
    {
        // ZIP download for all files in a recipe. GETs don't need
        // antiforgery; the route runs under the standard cookie auth +
        // EF tenant filter, so a user can only see recipes in their own
        // org. 404 collapses both "doesn't exist" and "exists in another
        // org" into the same response. Each file's RelativePath is joined
        // with `/` so ZipArchive materialises folders automatically.
        //
        // An optional `customer` query value: the download modal asks for it
        // and explains why, and we record the download against it
        // (RecordDownloadAsync) so a later bug in a recipe can be traced to who
        // received it. Optional because gating the download on it produced
        // "test" and "x" from anyone downloading for a demo — see issue #539.
        // We record BEFORE writing the ZIP body — once the stream starts the
        // status code is fixed. The recording GET has a side effect by design;
        // the download is a navigation and the trace is the point.
        //
        // The archive is built in memory first and copied out asynchronously.
        // ZipArchive writes its local headers and central directory with
        // synchronous Stream.Write, which Kestrel refuses (AllowSynchronousIO
        // is off), so writing straight into the response body sent a 200 and
        // then a truncated, unreadable file every time (#1212). Building it
        // first also means a failure here happens before anything is recorded
        // or sent. Recipes are text and small, so buffering is cheap - the
        // same trade WorkspaceZipBuilder makes.
        //
        // GETs can't carry an antiforgery token, so the attribution write would
        // otherwise be CSRF-reachable: another origin could navigate the
        // victim's session here and record a download for an arbitrary customer
        // string. Gate the write on the Sec-Fetch-Site fetch-metadata header,
        // failing closed: only `same-origin` (the modal's own location.assign)
        // and `none` (an address-bar navigation) record the attribution; every
        // other value — including a *missing* header, which older clients omit —
        // serves the ZIP but skips the write. See #414, #482.
        app.MapGet("/api/cookbook/{id:int}/download", async (
            int id,
            HttpContext ctx,
            RecipeService recipes,
            IOrganizationContext orgContext,
            CancellationToken ct) =>
        {
            var recipe = await recipes.GetAsync(id, ct);
            if (recipe is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using var zip = BuildArchive(recipe);

            var customer = (ctx.Request.Query["customer"].ToString() ?? string.Empty).Trim();
            // Record the attribution only on a navigation we can positively
            // attribute to this origin; a forged cross-site navigation (or a
            // request with no fetch-metadata at all) still gets the ZIP but no
            // write. The ZIP itself is served either way (it's org-scoped and
            // behind auth).
            var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
            var sameOriginNavigation =
                string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase);
            if (sameOriginNavigation)
            {
                await recipes.RecordDownloadAsync(id, customer, orgContext.CurrentUserId, ct);
            }

            var fileName = BuildArchiveFileName(recipe.Title, recipe.Id);
            WriteAttachmentHeaders(ctx, fileName);

            ctx.Response.ContentLength = zip.Length;
            await zip.CopyToAsync(ctx.Response.Body, ct);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Writes every file of <paramref name="recipe"/> into an in-memory ZIP and
    /// returns it rewound, ready to copy to the response. See the endpoint's
    /// comment for why this must not write to the response body directly.
    /// </summary>
    private static MemoryStream BuildArchive(Recipe recipe)
    {
        var stream = new MemoryStream();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in recipe.Files)
            {
                var entryPath = BuildSafeEntryPath(file.RelativePath, file.FileName);
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, utf8);
                writer.Write(file.Content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Builds a ZIP entry path from a recipe file's admin-authored
    /// <paramref name="relativePath"/> and <paramref name="fileName"/> that
    /// cannot escape the extraction directory on the downloader's machine
    /// (zip-slip). Both values come from the DB and an Editor controls them, so
    /// we can't trust them: separators are normalised, and empty, <c>.</c> and
    /// <c>..</c> segments are dropped before each surviving segment is sanitised
    /// the same way download filenames are (<see cref="EndpointHelpers.SanitiseFileName"/>).
    /// The <c>/</c> separators between real segments survive so <c>ZipArchive</c>
    /// still materialises the recipe's folder structure. See #481.
    /// </summary>
    /// <remarks>
    /// The rule itself moved to <see cref="RecipePaths.SafeEntryPath"/> when a
    /// recipe gained a second way out of the app — a commit into a GitHub
    /// repository (issue #626) — because a download and a pull request of the
    /// same recipe have to produce the same paths.
    /// </remarks>
    internal static string BuildSafeEntryPath(string? relativePath, string fileName) =>
        RecipePaths.SafeEntryPath(relativePath, fileName);

    /// <summary>
    /// Lower-cases and slugifies the recipe title for the ZIP filename.
    /// Falls back to <c>recipe-{id}</c> when the title slugifies to empty
    /// (titles made entirely of non-ASCII letters, punctuation, etc).
    /// </summary>
    internal static string BuildArchiveFileName(string title, int id)
    {
        var slug = RecipePaths.Slugify(title);
        if (string.IsNullOrEmpty(slug))
        {
            slug = $"recipe-{id}";
        }
        return slug + ".zip";
    }
}
