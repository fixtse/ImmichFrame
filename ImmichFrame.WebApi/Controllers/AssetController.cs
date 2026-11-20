using System.Globalization;
using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using ImmichFrame.WebApi.Helpers;
using ImmichFrame.WebApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers
{
    public class ImageResponse
    {
        public required string RandomImageBase64 { get; set; }
        public required string ThumbHashImageBase64 { get; set; }
        public required string PhotoDate { get; set; }
        public required string ImageLocation { get; set; }
    }


    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AssetController : ControllerBase
    {
        private readonly ILogger<AssetController> _logger;
        private readonly IImmichFrameLogic _logic;
        private readonly IGeneralSettings _settings;
        private readonly OpenNextionHistory _openNextionHistory;

        public AssetController(ILogger<AssetController> logger, IImmichFrameLogic logic, IGeneralSettings settings,
            OpenNextionHistory openNextionHistory)
        {
            _openNextionHistory = openNextionHistory;
            _logger = logger;
            _logic = logic;
            _settings = settings;
        }

        [HttpGet(Name = "GetAssets")]
        public async Task<List<AssetResponseDto>> GetAssets(string clientIdentifier = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Assets requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);
            return (await _logic.GetAssets()).ToList();
        }

        [HttpGet("{id}/AssetInfo", Name = "GetAssetInfo")]
        public async Task<AssetResponseDto> GetAssetInfo(Guid id, string clientIdentifier = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("AssetInfo '{id}' requested by '{sanitizedClientIdentifier}'", id, sanitizedClientIdentifier);

            return await _logic.GetAssetInfoById(id);
        }

        [HttpGet("{id}/AssetFaces", Name = "GetAssetFaces")]
        public async Task<IEnumerable<AssetFaceResponseDto>> GetAssetFaces(Guid id, string clientIdentifier = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("AssetFaces '{id}' requested by '{sanitizedClientIdentifier}'", id, sanitizedClientIdentifier);

            return await _logic.GetAssetFacesById(id);
        }

        [HttpGet("{id}/AlbumInfo", Name = "GetAlbumInfo")]
        public async Task<List<AlbumResponseDto>> GetAlbumInfo(Guid id, string clientIdentifier = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("AlbumInfo '{id}' requested by '{sanitizedClientIdentifier}'", id, sanitizedClientIdentifier);

            return (await _logic.GetAlbumInfoById(id)).ToList() ?? throw new AssetNotFoundException("No asset was found");
        }

        [Obsolete("Use GetAsset instead.")]
        [HttpGet("{id}/Image", Name = "GetImage")]
        [Produces("image/jpeg", "image/webp")]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetImage(Guid id, string clientIdentifier = "")
        {
            return await GetAsset(id, clientIdentifier, AssetTypeEnum.IMAGE);
        }

        [HttpGet("{id}/Asset", Name = "GetAsset")]
        [Produces("image/jpeg", "image/webp", "video/mp4", "video/quicktime")]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status206PartialContent)]
        [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
        public async Task<IActionResult> GetAsset(Guid id, string clientIdentifier = "", AssetTypeEnum? assetType = null)
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Asset '{id}' requested by '{sanitizedClientIdentifier}' (type hint: {assetType})", id, sanitizedClientIdentifier, assetType);

            var rangeHeader = Request.Headers["Range"].FirstOrDefault();

            AssetResponse asset;
            try
            {
                asset = await _logic.GetAsset(id, assetType, rangeHeader);
            }
            catch (ApiException ex) when (ex.StatusCode == 416)
            {
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            if (string.IsNullOrEmpty(rangeHeader))
            {
                var notification = new AssetRequestedNotification(id, sanitizedClientIdentifier);
                _ = _logic.SendWebhookNotification(notification);
            }

            Response.Headers["Accept-Ranges"] = "bytes";

            if (asset.IsPartial && !string.IsNullOrEmpty(asset.ContentRange))
            {
                Response.Headers["Content-Range"] = asset.ContentRange;
                Response.StatusCode = 206;
                Response.ContentType = asset.ContentType;

                if (asset.FileStream is { CanSeek: true } && asset.FileStream.Length > 0)
                    Response.ContentLength = asset.FileStream.Length;
                else if (asset.ContentLength.HasValue)
                    Response.ContentLength = asset.ContentLength;

                using (asset.Owner)
                {
                    await asset.FileStream.CopyToAsync(Response.Body);
                }
                return new EmptyResult();
            }

            if (asset.Owner != null)
                HttpContext.Response.RegisterForDispose(asset.Owner);

            return File(asset.FileStream, asset.ContentType, asset.FileName, enableRangeProcessing: true);
        }

        [HttpGet("OpenNextion")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [Produces("image/jpeg")]
        public async Task<IActionResult> GetOpenNextionImage(int width = 320, int height = 480, string fit = "contain",
            bool split = false, string info = "", bool back = false, CancellationToken cancellationToken = default)
        {
            if ((width, height) is not ((320, 480) or (480, 320) or (240, 320) or (320, 240)))
                return BadRequest("Use 320x480 or 240x320, or swap width and height for landscape.");
            OpenNextionFit mode;
            switch (fit)
            {
                case "contain": mode = OpenNextionFit.Contain; break;
                case "cover": mode = OpenNextionFit.Cover; break;
                case "blur": mode = OpenNextionFit.Blur; break;
                default: return BadRequest("Fit must be contain, cover or blur.");
            }

            var fields = OpenNextionFields(info);
            if (fields is null)
                return BadRequest("Info must be true, false or a list of date, location, album, description and people.");

            // Back steps through the screens already shown; the next request after
            // going back steps forward again before new photos are picked.
            // Photos come only from ImmichFrame's configured pool (accounts, albums,
            // people and filters); the history holds photos picked from it.
            var screenPortrait = height > width;
            var key = $"{width}x{height}|{fit}|{string.Join(',', fields.Order())}";
            var renderKey = key;
            var pickKey = $"{key}|{split}";
            var screen = _openNextionHistory.Step(back);
            if (screen is null)
            {
                screen = await _openNextionHistory.TakePrepared(pickKey)
                         ?? await PickOpenNextionScreen(split, screenPortrait);
                if (screen is null)
                    return Problem("No photo could be loaded.", statusCode: StatusCodes.Status502BadGateway);
                _openNextionHistory.Add(screen);
            }

            var jpeg = screen.Rendered(renderKey) ?? await RenderOpenNextion(screen, renderKey, width, height, mode, fields);
            if (jpeg is null)
                return Problem("The selected photo could not be decoded.", statusCode: StatusCodes.Status502BadGateway);
            foreach (var asset in screen.Assets)
                _ = _logic.SendWebhookNotification(new AssetRequestedNotification(asset.Id, "OpenNextion"));

            // Get the following screen ready while the panel shows this one.
            if (_openNextionHistory.AtNewest)
                _openNextionHistory.Prepare(pickKey, async () =>
                {
                    try
                    {
                        var next = await PickOpenNextionScreen(split, screenPortrait);
                        if (next != null) await RenderOpenNextion(next, renderKey, width, height, mode, fields);
                        return next;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Preparing the next OpenNextion photo failed");
                        return null;
                    }
                });
            return File(jpeg, "image/jpeg");
        }

        private async Task<byte[]?> RenderOpenNextion(OpenNextionScreen screen, string key, int width, int height,
            OpenNextionFit mode, IReadOnlySet<string> fields)
        {
            var sources = new List<OpenNextionSource>();
            var captions = new List<IReadOnlyList<string>>();
            try
            {
                foreach (var candidate in screen.Assets)
                {
                    var asset = await _logic.GetAsset(candidate.Id, AssetTypeEnum.IMAGE);
                    using var owner = asset.Owner;
                    await using var stream = asset.FileStream;
                    var source = await OpenNextionPhoto.Decode(stream, CancellationToken.None);
                    if (source is null) continue;
                    sources.Add(source);
                    captions.Add(fields.Count > 0 ? await OpenNextionCaption(candidate, fields) : []);
                }
                if (sources.Count == 0) return null;
                var jpeg = OpenNextionPhoto.Compose(sources, captions, width, height, mode);
                if (jpeg != null) screen.Store(key, jpeg);
                return jpeg;
            }
            finally
            {
                foreach (var source in sources) source.Dispose();
            }
        }

        private async Task<OpenNextionScreen?> PickOpenNextionScreen(bool split, bool screenPortrait)
        {
            var carried = _openNextionHistory.TakeCarry();
            var assets = new List<AssetResponseDto> { carried ?? await GetNextImage() };
            // Like ImmichFrame's split view: a photo whose orientation does not suit
            // the screen is paired with a second one of the same orientation.
            if (split && OpenNextionPortrait(assets[0]) is bool firstPortrait && firstPortrait != screenPortrait)
            {
                var second = await _logic.GetNextAsset();
                if (second is { Type: AssetTypeEnum.IMAGE } && second.Id != assets[0].Id)
                {
                    if (OpenNextionPortrait(second) == firstPortrait) assets.Add(second);
                    else _openNextionHistory.Carry(second);
                }
            }
            return new OpenNextionScreen(assets);
        }

        // Orientation from Immich's metadata, so a pairing candidate is not downloaded
        // just to measure it. Null when the size is unknown.
        internal static bool? OpenNextionPortrait(AssetResponseDto asset)
        {
            double width = asset.ExifInfo?.ExifImageWidth ?? 0, height = asset.ExifInfo?.ExifImageHeight ?? 0;
            if (width <= 0 || height <= 0) return null;
            if (asset.ExifInfo?.Orientation is "5" or "6" or "7" or "8") (width, height) = (height, width);
            return height > width;
        }

        internal static readonly string[] OpenNextionFieldNames = ["date", "location", "album", "description", "people"];

        // "true": the details ImmichFrame's own screen shows (its Show* settings);
        // "false" or empty: none; otherwise a comma list of field names (drawn in the order
        // date, place, album, description, people). Null when a name is unknown.
        internal IReadOnlySet<string>? OpenNextionFields(string? info)
        {
            var value = (info ?? string.Empty).Trim().ToLowerInvariant();
            if (value is "" or "false" or "none") return new HashSet<string>();
            if (value == "true")
            {
                var set = new HashSet<string>();
                if (_settings.ShowPhotoDate) set.Add("date");
                if (_settings.ShowImageLocation) set.Add("location");
                if (_settings.ShowAlbumName) set.Add("album");
                if (_settings.ShowImageDesc) set.Add("description");
                if (_settings.ShowPeopleDesc) set.Add("people");
                return set;
            }
            var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return names.All(n => OpenNextionFieldNames.Contains(n)) ? names.ToHashSet() : null;
        }

        // The details ImmichFrame's own screen shows, following its Show* settings.
        private async Task<IReadOnlyList<string>> OpenNextionCaption(AssetResponseDto asset, IReadOnlySet<string> fields)
        {
            var lines = new List<string>();
            try
            {
                if (fields.Contains("date"))
                {
                    var culture = new CultureInfo(_settings.Language);
                    var format = (_settings.PhotoDateFormat ?? "dd.MM.yyyy").Replace("''", "\\'");
                    lines.Add(asset.LocalDateTime.ToString(format, culture));
                }
                if (fields.Contains("location"))
                {
                    var parts = (_settings.ImageLocationFormat ?? "City,State,Country").Split(',')
                        .Select(p => p.Trim().ToLowerInvariant() switch
                        {
                            "city" => asset.ExifInfo?.City,
                            "state" => asset.ExifInfo?.State,
                            "country" => asset.ExifInfo?.Country,
                            _ => null
                        })
                        .Where(p => !string.IsNullOrWhiteSpace(p));
                    lines.Add(string.Join(", ", parts));
                }
                if (fields.Contains("album"))
                {
                    var albums = await _logic.GetAlbumInfoById(asset.Id);
                    lines.Add(string.Join(", ", albums.Select(a => a.AlbumName).Where(n => !string.IsNullOrWhiteSpace(n))));
                }
                if (fields.Contains("description"))
                    lines.Add(asset.ExifInfo?.Description ?? string.Empty);
                if (fields.Contains("people"))
                {
                    var people = asset.People;
                    if (people is null || people.Count == 0)
                        people = (await _logic.GetAssetInfoById(asset.Id)).People;
                    lines.Add(string.Join(", ", (people ?? []).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n))));
                }
            }
            catch (Exception ex)
            {
                // Details are optional: show the photo without them.
                _logger.LogWarning(ex, "OpenNextion photo details for '{id}' could not be loaded", asset.Id);
            }
            return lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        }

        private async Task<AssetResponseDto> GetNextImage()
        {
            const int maxAttempts = 10;
            for (int i = 0; i < maxAttempts; i++)
            {
                var candidate = await _logic.GetNextAsset();
                if (candidate == null) break;
                if (candidate.Type == AssetTypeEnum.IMAGE) return candidate;
            }

            throw new AssetNotFoundException("No image asset was found");
        }

        [HttpGet("RandomImageAndInfo", Name = "GetRandomImageAndInfo")]
        [Produces("application/json")]
        public async Task<ImageResponse> GetRandomImageAndInfo(string clientIdentifier = "")
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Random image requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);

            var randomAsset = await GetNextImage();
            var asset = await _logic.GetAsset(randomAsset.Id, AssetTypeEnum.IMAGE);
            var notification = new AssetRequestedNotification(randomAsset.Id, sanitizedClientIdentifier);
            _ = _logic.SendWebhookNotification(notification);

            string randomImageBase64;
            using (var memoryStream = new MemoryStream())
            {
                await asset.FileStream.CopyToAsync(memoryStream);
                randomImageBase64 = Convert.ToBase64String(memoryStream.ToArray());
            }

            randomAsset.ThumbhashImage!.Position = 0;
            byte[] byteArray = new byte[randomAsset.ThumbhashImage.Length];
            randomAsset.ThumbhashImage.Read(byteArray, 0, byteArray.Length);
            string thumbHashBase64 = Convert.ToBase64String(byteArray);

            CultureInfo cultureInfo = new CultureInfo(_settings.Language);
            string photoDateFormat = _settings.PhotoDateFormat!.Replace("''", "\\'");
            string photoDate = randomAsset.LocalDateTime.ToString(photoDateFormat, cultureInfo) ?? string.Empty;

            var locationFormat = _settings.ImageLocationFormat ?? "City,State,Country";
            var imageLocation = locationFormat
                .Replace("City", randomAsset.ExifInfo?.City ?? string.Empty)
                .Replace("State", randomAsset.ExifInfo?.State ?? string.Empty)
                .Replace("Country", randomAsset.ExifInfo?.Country ?? string.Empty);
            imageLocation = string.Join(",", imageLocation.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)));

            return new ImageResponse
            {
                RandomImageBase64 = randomImageBase64,
                ThumbHashImageBase64 = thumbHashBase64,
                PhotoDate = photoDate,
                ImageLocation = imageLocation
            };
        }
    }
}
