using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using TvDbSharper;
using TvDbSharper.Dto;

namespace Jellyfin.Plugin.Tvdb.Providers
{
    /// <summary>
    /// TvdbEpisodeProvider.
    /// </summary>
    public class TvdbEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TvdbEpisodeProvider> _logger;
        private readonly TvdbClientManager _tvdbClientManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbEpisodeProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{TvdbEpisodeProvider}"/> interface.</param>
        /// <param name="tvdbClientManager">Instance of <see cref="TvdbClientManager"/>.</param>
        public TvdbEpisodeProvider(IHttpClientFactory httpClientFactory, ILogger<TvdbEpisodeProvider> logger, TvdbClientManager tvdbClientManager)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _tvdbClientManager = tvdbClientManager;
        }

        /// <inheritdoc />
        public string Name => TvdbPlugin.ProviderName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            var list = new List<RemoteSearchResult>();

            // Either an episode number or date must be provided; and the dictionary of provider ids must be valid
            if ((searchInfo.IndexNumber == null && searchInfo.PremiereDate == null)
                || !TvdbSeriesProvider.IsValidSeries(searchInfo.SeriesProviderIds))
            {
                return list;
            }

            var metadataResult = await GetEpisode(searchInfo, cancellationToken).ConfigureAwait(false);

            if (!metadataResult.HasMetadata)
            {
                return list;
            }

            var item = metadataResult.Item;

            list.Add(new RemoteSearchResult
            {
                IndexNumber = item.IndexNumber,
                Name = item.Name,
                ParentIndexNumber = item.ParentIndexNumber,
                PremiereDate = item.PremiereDate,
                ProductionYear = item.ProductionYear,
                ProviderIds = item.ProviderIds,
                SearchProviderName = Name,
                IndexNumberEnd = item.IndexNumberEnd
            });

            return list;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>
            {
                QueriedById = true
            };

            if (TvdbSeriesProvider.IsValidSeries(info.SeriesProviderIds) &&
                (info.IndexNumber.HasValue || info.PremiereDate.HasValue))
            {
                // Check for multiple episodes per file, if not run one query.
                if (info.IndexNumberEnd.HasValue)
                {
                    _logger.LogDebug("Multiple episodes found in {Path}", info.Path);

                    result = await GetCombinedEpisode(info, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await GetEpisode(info, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogDebug("No series identity found for {EpisodeName}", info.Name);
            }

            return result;
        }

        private async Task<MetadataResult<Episode>> GetCombinedEpisode(EpisodeInfo info, CancellationToken cancellationToken)
        {
            var startIndex = info.IndexNumber;
            var endIndex = info.IndexNumberEnd;

            List<MetadataResult<Episode>> results = new List<MetadataResult<Episode>>();

            for (int? episode = startIndex; episode <= endIndex; episode++)
            {
                var tempEpisodeInfo = info;
                info.IndexNumber = episode;

                results.Add(await GetEpisode(tempEpisodeInfo, cancellationToken).ConfigureAwait(false));
            }

            var result = CombineResults(info, results);

            return result;
        }

        private MetadataResult<Episode> CombineResults(EpisodeInfo id, List<MetadataResult<Episode>> results)
        {
            // Use first result as baseline
            var result = results[0];

            var name = new StringBuilder(result.Item.Name);
            var overview = new StringBuilder(result.Item.Overview);

            for (int res = 1; res < results.Count; res++)
            {
                name.Append(" / ");
                name.Append(results[res].Item.Name);
                overview.Append(" / ");
                overview.Append(results[res].Item.Overview);
            }

            result.Item.Name = name.ToString();
            result.Item.Overview = overview.ToString();

            return result;
        }

        private async Task<MetadataResult<Episode>> GetEpisode(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Episode>
            {
                QueriedById = true
            };

            var seriesTvdbId = searchInfo.GetProviderId(TvdbPlugin.ProviderId);
            string? episodeTvdbId = null;
            try
            {
                episodeTvdbId = await _tvdbClientManager
                    .GetEpisodeTvdbId(searchInfo, searchInfo.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(episodeTvdbId))
                {
                    _logger.LogError(
                        "Episode {SeasonNumber}x{EpisodeNumber} not found for series {SeriesTvdbId}:{Name}",
                        searchInfo.ParentIndexNumber,
                        searchInfo.IndexNumber,
                        seriesTvdbId,
                        searchInfo.Name);
                    return result;
                }

                var episodeResult = await _tvdbClientManager.GetEpisodesAsync(
                    Convert.ToInt32(episodeTvdbId, CultureInfo.InvariantCulture),
                    searchInfo.MetadataLanguage,
                    cancellationToken).ConfigureAwait(false);

                result = MapEpisodeToResult(searchInfo, episodeResult.Data);
            }
            catch (TvDbServerException e)
            {
                _logger.LogError(
                    e,
                    "Failed to retrieve episode with id {EpisodeTvDbId}, series id {SeriesTvdbId}:{Name}",
                    episodeTvdbId,
                    seriesTvdbId,
                    searchInfo.Name);
            }

            return result;
        }

        private static MetadataResult<Episode> MapEpisodeToResult(EpisodeInfo id, EpisodeRecord episode)
        {
            var result = new MetadataResult<Episode>
            {
                HasMetadata = true,
                Item = new Episode
                {
                    IndexNumber = id.IndexNumber,
                    ParentIndexNumber = id.ParentIndexNumber,
                    IndexNumberEnd = id.IndexNumberEnd,
                    AirsBeforeEpisodeNumber = episode.AirsBeforeEpisode,
                    AirsAfterSeasonNumber = episode.AirsAfterSeason,
                    AirsBeforeSeasonNumber = episode.AirsBeforeSeason,
                    Name = episode.EpisodeName,
                    Overview = episode.Overview,
                    CommunityRating = (float?)episode.SiteRating,
                    OfficialRating = episode.ContentRating,
                }
            };
            result.ResetPeople();

            var item = result.Item;
            item.SetProviderId(TvdbPlugin.ProviderId, episode.Id.ToString(CultureInfo.InvariantCulture));
            item.SetProviderId(MetadataProvider.Imdb, episode.ImdbId);

            if (string.Equals(id.SeriesDisplayOrder, "dvd", StringComparison.OrdinalIgnoreCase))
            {
                item.IndexNumber = Convert.ToInt32(episode.DvdEpisodeNumber ?? episode.AiredEpisodeNumber, CultureInfo.InvariantCulture);
                item.ParentIndexNumber = episode.DvdSeason ?? episode.AiredSeason;
            }
            else if (string.Equals(id.SeriesDisplayOrder, "absolute", StringComparison.OrdinalIgnoreCase))
            {
                if (episode.AbsoluteNumber.GetValueOrDefault() != 0)
                {
                    item.IndexNumber = episode.AbsoluteNumber;
                }
            }
            else if (episode.AiredEpisodeNumber.HasValue)
            {
                item.IndexNumber = episode.AiredEpisodeNumber;
            }
            else if (episode.AiredSeason.HasValue)
            {
                item.ParentIndexNumber = episode.AiredSeason;
            }

            if (DateTime.TryParse(episode.FirstAired, out var date))
            {
                // dates from tvdb are UTC but without offset or Z
                item.PremiereDate = date;
                item.ProductionYear = date.Year;
            }

            foreach (var director in episode.Directors)
            {
                result.AddPerson(new PersonInfo
                {
                    Name = director,
                    Type = PersonType.Director
                });
            }

            // GuestStars is a weird list of names and roles
            // Example:
            // 1: Some Actor (Role1
            // 2: Role2
            // 3: Role3)
            // 4: Another Actor (Role1
            // ...
            for (var i = 0; i < episode.GuestStars.Length; ++i)
            {
                var currentActor = episode.GuestStars[i];
                var roleStartIndex = currentActor.IndexOf('(', StringComparison.Ordinal);

                if (roleStartIndex == -1)
                {
                    result.AddPerson(new PersonInfo
                    {
                        Type = PersonType.GuestStar,
                        Name = currentActor,
                        Role = string.Empty
                    });
                    continue;
                }

                var roles = new List<string> { currentActor.Substring(roleStartIndex + 1) };

                // Fetch all roles
                for (var j = i + 1; j < episode.GuestStars.Length; ++j)
                {
                    var currentRole = episode.GuestStars[j];
                    var roleEndIndex = currentRole.Contains(')', StringComparison.Ordinal);

                    if (!roleEndIndex)
                    {
                        roles.Add(currentRole);
                        continue;
                    }

                    roles.Add(currentRole.TrimEnd(')'));
                    // Update the outer index (keep in mind it adds 1 after the iteration)
                    i = j;
                    break;
                }

                result.AddPerson(new PersonInfo
                {
                    Type = PersonType.GuestStar,
                    Name = currentActor.Substring(0, roleStartIndex).Trim(),
                    Role = string.Join(", ", roles)
                });
            }

            foreach (var writer in episode.Writers)
            {
                result.AddPerson(new PersonInfo
                {
                    Name = writer,
                    Type = PersonType.Writer
                });
            }

            result.ResultLanguage = episode.Language.EpisodeName;
            return result;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }
    }
}
