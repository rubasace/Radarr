using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.PreDB;
using NzbDrone.Core.MetadataSource.RadarrAPI;
using NzbDrone.Core.MetadataSource.SkyHook.Resource;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.AlternativeTitles;
using NzbDrone.Core.Movies.Credits;
using NzbDrone.Core.NetImport.ImportExclusions;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.MetadataSource.SkyHook
{
    public class SkyHookProxy : IProvideMovieInfo, ISearchForNewMovie, IDiscoverNewMovies
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        private readonly IHttpRequestBuilderFactory _movieBuilder;
        private readonly ITmdbConfigService _configService;
        private readonly IMovieService _movieService;
        private readonly IPreDBService _predbService;
        private readonly IImportExclusionsService _exclusionService;
        private readonly IRadarrAPIClient _radarrAPI;

        public SkyHookProxy(IHttpClient httpClient,
            IRadarrCloudRequestBuilder requestBuilder,
            ITmdbConfigService configService,
            IMovieService movieService,
            IPreDBService predbService,
            IImportExclusionsService exclusionService,
            IRadarrAPIClient radarrAPI,
            Logger logger)
        {
            _httpClient = httpClient;
            _movieBuilder = requestBuilder.TMDB;
            _configService = configService;
            _movieService = movieService;
            _predbService = predbService;
            _exclusionService = exclusionService;
            _radarrAPI = radarrAPI;

            _logger = logger;
        }

        public HashSet<int> GetChangedMovies(DateTime startTime)
        {
            var startDate = startTime.ToString("o");

            var request = _movieBuilder.Create()
                .SetSegment("api", "3")
                .SetSegment("route", "movie")
                .SetSegment("id", "")
                .SetSegment("secondaryRoute", "changes")
                .AddQueryParam("start_date", startDate)
                .Build();

            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            var response = _httpClient.Get<MovieSearchRootResource>(request);

            return new HashSet<int>(response.Resource.Results.Select(c => c.Id));
        }

        public Tuple<Movie, List<Credit>> GetMovieInfo(int tmdbId, bool hasPreDBEntry)
        {
            var langCode = "en";

            var request = _movieBuilder.Create()
               .SetSegment("api", "3")
               .SetSegment("route", "movie")
               .SetSegment("id", tmdbId.ToString())
               .SetSegment("secondaryRoute", "")
               .AddQueryParam("append_to_response", "alternative_titles,release_dates,videos,credits,translations")
               .AddQueryParam("language", langCode.ToUpper())

               // .AddQueryParam("country", "US")
               .Build();

            request.AllowAutoRedirect = true;
            request.SuppressHttpError = true;

            var response = _httpClient.Get<MovieResourceRoot>(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new MovieNotFoundException(tmdbId);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpException(request, response);
            }

            if (response.Headers.ContentType != HttpAccept.JsonCharset.Value)
            {
                throw new HttpException(request, response);
            }

            // The dude abides, so should us, Lets be nice to TMDb
            // var allowed = int.Parse(response.Headers.GetValues("X-RateLimit-Limit").First()); // get allowed
            // var reset = long.Parse(response.Headers.GetValues("X-RateLimit-Reset").First()); // get time when it resets
            if (response.Headers.ContainsKey("X-RateLimit-Remaining"))
            {
                var remaining = int.Parse(response.Headers.GetValues("X-RateLimit-Remaining").First());
                if (remaining <= 5)
                {
                    _logger.Trace("Waiting 5 seconds to get information for the next 35 movies");
                    Thread.Sleep(5000);
                }
            }

            var resource = response.Resource;
            if (resource.Status_message != null)
            {
                if (resource.Status_code == 34)
                {
                    _logger.Warn("Movie with TmdbId {0} could not be found. This is probably the case when the movie was deleted from TMDB.", tmdbId);
                    return null;
                }

                _logger.Warn(resource.Status_message);
                return null;
            }

            var movie = new Movie();
            var altTitles = new List<AlternativeTitle>();

            foreach (var alternativeTitle in resource.Alternative_titles.Titles)
            {
                if (alternativeTitle.Iso_3166_1.ToLower() == langCode)
                {
                    altTitles.Add(new AlternativeTitle(alternativeTitle.Title, SourceType.TMDB, tmdbId, IsoLanguages.Find(alternativeTitle.Iso_3166_1.ToLower())?.Language ?? Language.English));
                }
                else if (alternativeTitle.Iso_3166_1.ToLower() == "us")
                {
                    altTitles.Add(new AlternativeTitle(alternativeTitle.Title, SourceType.TMDB, tmdbId, Language.English));
                }
            }

            foreach (var translation in resource.Translations.Translations)
            {
                var translationLanguage = IsoLanguages.Find(translation.Iso_3166_1.ToLower());

                if (translationLanguage != null && translation.Data.Title.IsNotNullOrWhiteSpace())
                {
                    altTitles.Add(new AlternativeTitle(translation.Data.Title, SourceType.Translation, tmdbId, translationLanguage.Language));
                }
            }

            movie.TmdbId = tmdbId;
            movie.ImdbId = resource.Imdb_id;
            movie.Title = resource.Original_title;
            movie.TitleSlug = Parser.Parser.ToUrlSlug(resource.Original_title);
            movie.CleanTitle = resource.Original_title.CleanSeriesTitle();
            movie.SortTitle = Parser.Parser.NormalizeTitle(resource.Original_title);
            movie.Overview = resource.Overview;
            movie.Website = resource.Homepage;

            if (resource.Release_date.IsNotNullOrWhiteSpace())
            {
                movie.InCinemas = DateTime.Parse(resource.Release_date);

                movie.Year = movie.InCinemas.Value.Year;
            }

            movie.TitleSlug += "-" + movie.TmdbId.ToString();

            movie.Images.AddIfNotNull(MapImage(resource.Poster_path, MediaCoverTypes.Poster)); //TODO: Update to load image specs from tmdb page!
            movie.Images.AddIfNotNull(MapImage(resource.Backdrop_path, MediaCoverTypes.Fanart));
            movie.Runtime = resource.Runtime;

            //foreach(Title title in resource.alternative_titles.titles)
            //{
            //    movie.AlternativeTitles.Add(title.title);
            //}
            foreach (ReleaseDatesLanguageResource releaseDates in resource.Release_dates.Results)
            {
                foreach (ReleaseDateResource releaseDate in releaseDates.Release_dates)
                {
                    if (releaseDate.Type == 5 || releaseDate.Type == 4)
                    {
                        if (movie.PhysicalRelease.HasValue)
                        {
                            if (movie.PhysicalRelease.Value.After(DateTime.Parse(releaseDate.Release_date)))
                            {
                                movie.PhysicalRelease = DateTime.Parse(releaseDate.Release_date); //Use oldest release date available.
                                movie.PhysicalReleaseNote = releaseDate.Note;
                            }
                        }
                        else
                        {
                            movie.PhysicalRelease = DateTime.Parse(releaseDate.Release_date);
                            movie.PhysicalReleaseNote = releaseDate.Note;
                        }
                    }
                }
            }

            movie.Ratings = new Ratings();
            movie.Ratings.Votes = resource.Vote_count;
            movie.Ratings.Value = (decimal)resource.Vote_average;

            foreach (GenreResource genre in resource.Genres)
            {
                movie.Genres.Add(genre.Name);
            }

            var now = DateTime.Now;

            //handle the case when we have both theatrical and physical release dates
            if (movie.InCinemas.HasValue && movie.PhysicalRelease.HasValue)
            {
                if (now < movie.InCinemas)
                {
                    movie.Status = MovieStatusType.Announced;
                }
                else if (now >= movie.InCinemas)
                {
                    movie.Status = MovieStatusType.InCinemas;
                }

                if (now >= movie.PhysicalRelease)
                {
                    movie.Status = MovieStatusType.Released;
                }
            }

            //handle the case when we have theatrical release dates but we dont know the physical release date
            else if (movie.InCinemas.HasValue && (now >= movie.InCinemas))
            {
                movie.Status = MovieStatusType.InCinemas;
            }

            //handle the case where we only have a physical release date
            else if (movie.PhysicalRelease.HasValue && (now >= movie.PhysicalRelease))
            {
                movie.Status = MovieStatusType.Released;
            }

            //otherwise the title has only been announced
            else
            {
                movie.Status = MovieStatusType.Announced;
            }

            //since TMDB lacks alot of information lets assume that stuff is released if its been in cinemas for longer than 3 months.
            if (!movie.PhysicalRelease.HasValue && (movie.Status == MovieStatusType.InCinemas) && (DateTime.Now.Subtract(movie.InCinemas.Value).TotalSeconds > 60 * 60 * 24 * 30 * 3))
            {
                movie.Status = MovieStatusType.Released;
            }

            if (!hasPreDBEntry)
            {
                if (_predbService.HasReleases(movie))
                {
                    movie.HasPreDBEntry = true;
                }
                else
                {
                    movie.HasPreDBEntry = false;
                }
            }

            if (resource.Videos != null)
            {
                foreach (VideoResource video in resource.Videos.Results)
                {
                    if (video.Type == "Trailer" && video.Site == "YouTube")
                    {
                        if (video.Key != null)
                        {
                            movie.YouTubeTrailerId = video.Key;
                            break;
                        }
                    }
                }
            }

            if (resource.Production_companies != null)
            {
                if (resource.Production_companies.Any())
                {
                    movie.Studio = resource.Production_companies[0].Name;
                }
            }

            movie.AlternativeTitles.AddRange(altTitles);

            movie.OriginalLanguage = resource.Original_language;

            var people = new List<Credit>();

            people.AddRange(resource.Credits.Cast.Select(MapCast).ToList());
            people.AddRange(resource.Credits.Crew.Select(MapCrew).ToList());

            if (resource.Belongs_to_collection != null)
            {
                movie.Collection = MapCollection(resource.Belongs_to_collection);

                movie.Collection.Images.AddIfNotNull(MapImage(resource.Belongs_to_collection.Poster_path, MediaCoverTypes.Poster));
                movie.Collection.Images.AddIfNotNull(MapImage(resource.Belongs_to_collection.Backdrop_path, MediaCoverTypes.Fanart));
            }

            return new Tuple<Movie, List<Credit>>(movie, people);
        }

        public Movie GetMovieInfo(string imdbId)
        {
            var request = _movieBuilder.Create()
                .SetSegment("api", "3")
                .SetSegment("route", "find")
                .SetSegment("id", imdbId)
                .SetSegment("secondaryRoute", "")
                .AddQueryParam("external_source", "imdb_id")
                .Build();

            request.AllowAutoRedirect = true;

            // request.SuppressHttpError = true;
            var response = _httpClient.Get<FindRootResource>(request);

            if (response.HasHttpError)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new MovieNotFoundException(imdbId);
                }
                else
                {
                    throw new HttpException(request, response);
                }
            }

            // The dude abides, so should us, Lets be nice to TMDb
            // var allowed = int.Parse(response.Headers.GetValues("X-RateLimit-Limit").First()); // get allowed
            // var reset = long.Parse(response.Headers.GetValues("X-RateLimit-Reset").First()); // get time when it resets
            if (response.Headers.ContainsKey("X-RateLimit-Remaining"))
            {
                var remaining = int.Parse(response.Headers.GetValues("X-RateLimit-Remaining").First());
                if (remaining <= 5)
                {
                    _logger.Trace("Waiting 5 seconds to get information for the next 35 movies");
                    Thread.Sleep(5000);
                }
            }

            if (!response.Resource.Movie_results.Any())
            {
                throw new MovieNotFoundException(imdbId);
            }

            return MapMovie(response.Resource.Movie_results.First());
        }

        public List<Movie> DiscoverNewMovies(string action)
        {
            var allMovies = _movieService.GetAllMovies();
            var allExclusions = _exclusionService.GetAllExclusions();
            string allIds = string.Join(",", allMovies.Select(m => m.TmdbId));
            string ignoredIds = string.Join(",", allExclusions.Select(ex => ex.TmdbId));

            List<MovieResultResource> results = new List<MovieResultResource>();

            try
            {
                results = _radarrAPI.DiscoverMovies(action, (request) =>
                {
                    request.AllowAutoRedirect = true;
                    request.Method = HttpMethod.POST;
                    request.Headers.ContentType = "application/x-www-form-urlencoded";
                    request.SetContent($"tmdbIds={allIds}&ignoredIds={ignoredIds}");
                    return request;
                });

                results = results.Where(m => allMovies.None(mo => mo.TmdbId == m.Id) && allExclusions.None(ex => ex.TmdbId == m.Id)).ToList();
            }
            catch (RadarrAPIException exception)
            {
                _logger.Error(exception, "Failed to discover movies for action {0}!", action);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Failed to discover movies for action {0}!", action);
            }

            return results.SelectList(MapMovie);
        }

        private string StripTrailingTheFromTitle(string title)
        {
            if (title.EndsWith(",the"))
            {
                title = title.Substring(0, title.Length - 4);
            }
            else if (title.EndsWith(", the"))
            {
                title = title.Substring(0, title.Length - 5);
            }

            return title;
        }

        public List<Movie> SearchForNewMovie(string title)
        {
            try
            {
                var lowerTitle = title.ToLower();

                lowerTitle = lowerTitle.Replace(".", "");

                var parserResult = Parser.Parser.ParseMovieTitle(title, true, true);

                var yearTerm = "";

                if (parserResult != null && parserResult.MovieTitle != title)
                {
                    //Parser found something interesting!
                    lowerTitle = parserResult.MovieTitle.ToLower().Replace(".", " "); //TODO Update so not every period gets replaced (e.g. R.I.P.D.)
                    if (parserResult.Year > 1800)
                    {
                        yearTerm = parserResult.Year.ToString();
                    }

                    if (parserResult.ImdbId.IsNotNullOrWhiteSpace())
                    {
                        try
                        {
                            return new List<Movie> { GetMovieInfo(parserResult.ImdbId) };
                        }
                        catch (Exception)
                        {
                            return new List<Movie>();
                        }
                    }
                }

                lowerTitle = StripTrailingTheFromTitle(lowerTitle);

                if (lowerTitle.StartsWith("imdb:") || lowerTitle.StartsWith("imdbid:"))
                {
                    var slug = lowerTitle.Split(':')[1].Trim();

                    string imdbid = slug;

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace))
                    {
                        return new List<Movie>();
                    }

                    try
                    {
                        return new List<Movie> { GetMovieInfo(imdbid) };
                    }
                    catch (MovieNotFoundException)
                    {
                        return new List<Movie>();
                    }
                }

                if (lowerTitle.StartsWith("tmdb:") || lowerTitle.StartsWith("tmdbid:"))
                {
                    var slug = lowerTitle.Split(':')[1].Trim();

                    int tmdbid = -1;

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace) || !int.TryParse(slug, out tmdbid))
                    {
                        return new List<Movie>();
                    }

                    try
                    {
                        return new List<Movie> { GetMovieInfo(tmdbid, false).Item1 };
                    }
                    catch (MovieNotFoundException)
                    {
                        return new List<Movie>();
                    }
                }

                var searchTerm = lowerTitle.Replace("_", "+").Replace(" ", "+").Replace(".", "+");

                var firstChar = searchTerm.First();

                var request = _movieBuilder.Create()
                    .SetSegment("api", "3")
                    .SetSegment("route", "search")
                    .SetSegment("id", "movie")
                    .SetSegment("secondaryRoute", "")
                    .AddQueryParam("query", searchTerm)
                    .AddQueryParam("year", yearTerm)
                    .AddQueryParam("include_adult", false)
                    .Build();

                request.AllowAutoRedirect = true;
                request.SuppressHttpError = true;

                var response = _httpClient.Get<MovieSearchRootResource>(request);

                var movieResults = response.Resource.Results;

                return movieResults.SelectList(MapSearchResult);
            }
            catch (HttpException)
            {
                throw new SkyHookException("Search for '{0}' failed. Unable to communicate with TMDb.", title);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new SkyHookException("Search for '{0}' failed. Invalid response received from TMDb.", title);
            }
        }

        private Movie MapSearchResult(MovieResultResource result)
        {
            var movie = _movieService.FindByTmdbId(result.Id);

            if (movie == null)
            {
                movie = MapMovie(result);
            }

            return movie;
        }

        public Movie MapMovie(MovieResultResource result)
        {
            var imdbMovie = new Movie();
            imdbMovie.TmdbId = result.Id;
            try
            {
                imdbMovie.SortTitle = Parser.Parser.NormalizeTitle(result.Title);
                imdbMovie.Title = result.Title;
                imdbMovie.TitleSlug = Parser.Parser.ToUrlSlug(result.Title);

                try
                {
                    if (result.Release_date.IsNotNullOrWhiteSpace())
                    {
                        imdbMovie.InCinemas = DateTime.ParseExact(result.Release_date, "yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);
                        imdbMovie.Year = imdbMovie.InCinemas.Value.Year;
                    }

                    if (result.Physical_release.IsNotNullOrWhiteSpace())
                    {
                        imdbMovie.PhysicalRelease = DateTime.ParseExact(result.Physical_release, "yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);
                        if (result.Physical_release_note.IsNotNullOrWhiteSpace())
                        {
                            imdbMovie.PhysicalReleaseNote = result.Physical_release_note;
                        }
                    }
                }
                catch (Exception)
                {
                    _logger.Debug("Not a valid date time.");
                }

                var now = DateTime.Now;

                //handle the case when we have both theatrical and physical release dates
                if (imdbMovie.InCinemas.HasValue && imdbMovie.PhysicalRelease.HasValue)
                {
                    if (now < imdbMovie.InCinemas)
                    {
                        imdbMovie.Status = MovieStatusType.Announced;
                    }
                    else if (now >= imdbMovie.InCinemas)
                    {
                        imdbMovie.Status = MovieStatusType.InCinemas;
                    }

                    if (now >= imdbMovie.PhysicalRelease)
                    {
                        imdbMovie.Status = MovieStatusType.Released;
                    }
                }

                //handle the case when we have theatrical release dates but we dont know the physical release date
                else if (imdbMovie.InCinemas.HasValue && (now >= imdbMovie.InCinemas))
                {
                    imdbMovie.Status = MovieStatusType.InCinemas;
                }

                //handle the case where we only have a physical release date
                else if (imdbMovie.PhysicalRelease.HasValue && (now >= imdbMovie.PhysicalRelease))
                {
                    imdbMovie.Status = MovieStatusType.Released;
                }

                //otherwise the title has only been announced
                else
                {
                    imdbMovie.Status = MovieStatusType.Announced;
                }

                //since TMDB lacks alot of information lets assume that stuff is released if its been in cinemas for longer than 3 months.
                if (!imdbMovie.PhysicalRelease.HasValue && (imdbMovie.Status == MovieStatusType.InCinemas) && (DateTime.Now.Subtract(imdbMovie.InCinemas.Value).TotalSeconds > 60 * 60 * 24 * 30 * 3))
                {
                    imdbMovie.Status = MovieStatusType.Released;
                }

                imdbMovie.TitleSlug += "-" + imdbMovie.TmdbId;

                imdbMovie.Images = new List<MediaCover.MediaCover>();
                imdbMovie.Overview = result.Overview;
                imdbMovie.Ratings = new Ratings { Value = (decimal)result.Vote_average, Votes = result.Vote_count };

                try
                {
                    imdbMovie.Images.AddIfNotNull(MapImage(result.Poster_path, MediaCoverTypes.Poster));
                }
                catch (Exception)
                {
                    _logger.Debug(result);
                }

                if (result.Trailer_key.IsNotNullOrWhiteSpace() && result.Trailer_site.IsNotNullOrWhiteSpace())
                {
                    if (result.Trailer_site == "youtube")
                    {
                        imdbMovie.YouTubeTrailerId = result.Trailer_key;
                    }
                }

                return imdbMovie;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error occured while searching for new movies.");
            }

            return null;
        }

        private static Credit MapCast(CastResource arg)
        {
            var newActor = new Credit
            {
                Name = arg.Name,
                Character = arg.Character,
                Order = arg.Order,
                CreditTmdbId = arg.Credit_Id,
                PersonTmdbId = arg.Id,
                Type = CreditType.Cast
            };

            if (arg.Profile_Path != null)
            {
                newActor.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover(MediaCoverTypes.Headshot, "https://image.tmdb.org/t/p/original" + arg.Profile_Path)
                };
            }

            return newActor;
        }

        private static Credit MapCrew(CrewResource arg)
        {
            var newActor = new Credit
            {
                Name = arg.Name,
                Department = arg.Department,
                Job = arg.Job,
                CreditTmdbId = arg.Credit_Id,
                PersonTmdbId = arg.Id,
                Type = CreditType.Crew
            };

            if (arg.Profile_Path != null)
            {
                newActor.Images = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover(MediaCoverTypes.Headshot, "https://image.tmdb.org/t/p/original" + arg.Profile_Path)
                };
            }

            return newActor;
        }

        private static MovieCollection MapCollection(CollectionResource arg)
        {
            var newCollection = new MovieCollection
            {
                Name = arg.Name,
                TmdbId = arg.Id,
            };

            return newCollection;
        }

        private MediaCover.MediaCover MapImage(string path, MediaCoverTypes type)
        {
            if (path.IsNotNullOrWhiteSpace())
            {
                return _configService.GetCoverForURL(path, type);
            }

            return null;
        }

        public Movie MapMovieToTmdbMovie(Movie movie)
        {
            try
            {
                Movie newMovie = movie;
                if (movie.TmdbId > 0)
                {
                    newMovie = GetMovieInfo(movie.TmdbId, false).Item1;
                }
                else if (movie.ImdbId.IsNotNullOrWhiteSpace())
                {
                    newMovie = GetMovieInfo(movie.ImdbId);
                }
                else
                {
                    var yearStr = "";
                    if (movie.Year > 1900)
                    {
                        yearStr = $" {movie.Year}";
                    }

                    newMovie = SearchForNewMovie(movie.Title + yearStr).FirstOrDefault();
                }

                if (newMovie == null)
                {
                    _logger.Warn("Couldn't map movie {0} to a movie on The Movie DB. It will not be added :(", movie.Title);
                    return null;
                }

                newMovie.Path = movie.Path;
                newMovie.RootFolderPath = movie.RootFolderPath;
                newMovie.ProfileId = movie.ProfileId;
                newMovie.Monitored = movie.Monitored;
                newMovie.MovieFile = movie.MovieFile;
                newMovie.MinimumAvailability = movie.MinimumAvailability;
                newMovie.Tags = movie.Tags;

                return newMovie;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't map movie {0} to a movie on The Movie DB. It will not be added :(", movie.Title);
                return null;
            }
        }
    }
}
