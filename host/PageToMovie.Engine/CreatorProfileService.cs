using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

public sealed class CreatorProfileService
{
    private readonly UserDatabaseService _users;
    private readonly DemoCatalogService _demos;
    private readonly DemoUpvoteService _upvotes;
    private readonly ProjectStore _projects;

    public CreatorProfileService(
        UserDatabaseService users,
        DemoCatalogService demos,
        DemoUpvoteService upvotes,
        ProjectStore projects,
        ILogger<CreatorProfileService> logger)
    {
        _users = users;
        _demos = demos;
        _upvotes = upvotes;
        _projects = projects;
    }

    public async Task<CreatorProfileDto?> GetProfileAsync(string handleOrId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(handleOrId)) return null;
        var cleanHandle = handleOrId.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(cleanHandle)) return null;

        var user = await _users.ResolveUserAsync(cleanHandle, ct).ConfigureAwait(false);
        string userId = user?.UserId ?? cleanHandle;
        string username = user?.Username ?? cleanHandle;

        // 1. Movies published (public demos created by this user)
        var allPublicDemos = await _demos.ListPublicAsync(take: 200, ct: ct).ConfigureAwait(false);
        var userDemos = allPublicDemos
            .Where(d => string.Equals(d.CreatedBy, userId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.CreatedBy, username, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int moviesPublished = userDemos.Count;

        // 2. Total upvotes received across user's demos
        var demoIds = userDemos.Select(d => d.Id).ToList();
        var counts = await _upvotes.GetCountsAsync(demoIds, ct).ConfigureAwait(false);
        int totalUpvotes = counts.Values.Sum();

        // 3. Community forks spawned from user's projects
        var allProjects = await _projects.ListProjectsAsync(ct).ConfigureAwait(false) ?? (IReadOnlyList<ProjectInfo>)Array.Empty<ProjectInfo>();
        var userProjectIds = allProjects
            .Where(p => string.Equals(p.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.OwnerUserId, username, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int forksSpawned = allProjects.Count(p =>
            !string.IsNullOrWhiteSpace(p.ParentProjectId) &&
            userProjectIds.Contains(p.ParentProjectId));

        // 4. Compute Creator Badges
        var badges = new List<CreatorBadgeDto>();
        if (moviesPublished >= 1)
        {
            badges.Add(new CreatorBadgeDto
            {
                Id = "debut_director",
                Name = "Debut Director",
                Icon = "🌟",
                Description = "Published at least 1 public AI movie demo"
            });
        }
        if (totalUpvotes >= 100 || (moviesPublished >= 1 && totalUpvotes >= 1))
        {
            badges.Add(new CreatorBadgeDto
            {
                Id = "featured_filmmaker",
                Name = "Featured Filmmaker",
                Icon = "🎬",
                Description = "Earned community upvotes on published movies"
            });
        }
        if (forksSpawned >= 1)
        {
            badges.Add(new CreatorBadgeDto
            {
                Id = "open_source_pioneer",
                Name = "Open Source Pioneer",
                Icon = "🍴",
                Description = "Spawned open-source community project forks"
            });
        }

        return new CreatorProfileDto
        {
            UserId = userId,
            Username = username,
            MoviesPublished = moviesPublished,
            TotalUpvotes = totalUpvotes,
            ForksSpawned = forksSpawned,
            Badges = badges
        };
    }
}
