using System;
using System.Linq;
using System.Threading.Tasks;
using Kroeg.Server.Models;
using Kroeg.Server.Services;
using Kroeg.Server.Services.EntityStore;
using Microsoft.AspNetCore.Mvc;

namespace Kroeg.Server.Controllers
{
    [Route("/api/v1/")]
    public class MastodonController : Controller
    {
        private readonly IEntityStore _entityStore;
        private readonly RelevantEntitiesService _relevantEntities;
        private readonly CollectionTools _collectionTools;

        public MastodonController(IEntityStore entityStore, RelevantEntitiesService relevantEntities, CollectionTools collectionTools)
        {
            _entityStore = entityStore;
            _relevantEntities = relevantEntities;
            _collectionTools = collectionTools;
        }

        private async Task<Mastodon.Account> _processAccount(APEntity entity)
        {
            var result = new Mastodon.Account
            {
                id = Uri.EscapeDataString(entity.Id),
                username = (string) entity.Data["preferredUsername"].FirstOrDefault()?.Primitive ?? entity.Id,
                display_name = (string) entity.Data["name"].FirstOrDefault()?.Primitive ?? entity.Id,
                locked = entity.Data["manuallyApprovesFollowers"].Any(a => (bool) a.Primitive),
                created_at = DateTime.Now,
                note = (string) entity.Data["summary"].FirstOrDefault()?.Primitive ?? "",
                url = (string) entity.Data["url"].FirstOrDefault()?.Primitive ?? entity.Id,
                moved = null,

                followers_count = -1,
                following_count = -1,
                statuses_count = -1
            };

            if (entity.IsOwner) result.acct = result.username;
            else
                result.acct = result.username + "@" + (new Uri(entity.Id)).Host;

            if (entity.Data["icon"].Any())
                result.avatar = result.avatar_static = entity.Data["icon"].First().Id ?? entity.Data["icon"].First().SubObject["url"].First().Id;

            var followers = await _entityStore.GetEntity(entity.Data["followers"].First().Id, false);
            if (followers != null && followers.Data["totalItems"].Any())
                result.followers_count = (int) followers.Data["totalItems"].First().Primitive;                

            var following = await _entityStore.GetEntity(entity.Data["following"].First().Id, false);
            if (following != null && following.Data["totalItems"].Any())
                result.following_count = (int) following.Data["totalItems"].First().Primitive;                

            var outbox = await _entityStore.GetEntity(entity.Data["outbox"].First().Id, false);
            if (outbox != null && outbox.Data["totalItems"].Any())
                result.statuses_count = (int) outbox.Data["totalItems"].First().Primitive;

            return result;         
        }

        private async Task<Mastodon.Status> _translateNote(APEntity note, string id)
        {
            if (note == null) return null;
            if (note.Type != "https://www.w3.org/ns/activitystreams#Note") return null;

            var attributed = await _entityStore.GetEntity(note.Data["attributedTo"].First().Id, true);

            var status = new Mastodon.Status
            {
                id = id ?? Uri.EscapeDataString(note.Id),
                uri = note.Id,
                url = note.Data["url"].FirstOrDefault()?.Id ?? note.Id,
                account = await _processAccount(attributed),
                in_reply_to_id = note.Data["inReplyTo"].FirstOrDefault()?.Id,
                reblog = null,
                content = (string) note.Data["content"].First().Primitive,
                created_at = DateTime.Parse((string) note.Data["published"].First().Primitive ?? DateTime.Now.ToString()),
                emojis = new string[] {},
                reblogs_count = 0,
                favourites_count = 0,
                reblogged = false,
                favourited = false,
                muted = false,
                sensitive = note.Data["sensitive"].Any(a => (bool) a.Primitive),
                spoiler_text = (string) note.Data["summary"].FirstOrDefault()?.Primitive,
                visibility = note.Data["to"].Any(a => a.Id == "https://www.w3.org/ns/activitystreams#Public") ? "public"
                            : note.Data["cc"].Any(a => a.Id == "https://www.w3.org/ns/activitystreams#Public") ? "unlisted"
                            : note.Data["to"].Any(a => a.Id == attributed.Data["followers"].First().Id) ? "private"
                            : "direct",
                media_attachments = new string[] {},
                mentions = new string[] {},
                tags = new string[] {},
                application = new Mastodon.Application { Name = "Kroeg", Website = "https://puckipedia.com/kroeg" },
                language = null,
                pinned = false
            };

            return status;
        }

        private async Task<Mastodon.Status> _translateStatus(CollectionTools.EntityCollectionItem item)
        {
            var isCreate = item.Entity.Data.Type.Contains("https://www.w3.org/ns/activitystreams#Create");
            var isAnnounce = item.Entity.Data.Type.Contains("https://www.w3.org/ns/activitystreams#Announce");
            if (!isCreate && !isAnnounce) return null;

            var inner = await _translateNote(await _entityStore.GetEntity(item.Entity.Data["object"].First().Id, true), (isCreate && item.CollectionItemId >= 0) ? item.CollectionItemId.ToString() : null);
            if (inner == null) return null;

            if (isCreate) return inner;

            return new Mastodon.Status
            {
                id = item.CollectionItemId.ToString(),
                uri = inner.uri,
                url = inner.url,
                account = await _processAccount(await _entityStore.GetEntity(item.Entity.Data["actor"].First().Id, true)),
                in_reply_to_id = inner.in_reply_to_id,
                in_reply_to_account_id = inner.in_reply_to_account_id,
                reblog = inner,
                content = inner.content,
                created_at = DateTime.Parse((string) item.Entity.Data["published"].First().Primitive ?? DateTime.Now.ToString()),
                emojis = inner.emojis,
                reblogs_count = inner.reblogs_count,
                favourites_count = inner.favourites_count,
                reblogged = inner.reblogged,
                favourited = inner.favourited,
                muted = inner.muted,
                sensitive = inner.sensitive,
                spoiler_text = inner.spoiler_text,
                visibility = inner.visibility,
                media_attachments = inner.media_attachments,
                mentions = inner.mentions,
                tags = inner.tags,
                application = inner.application,
                language = inner.language,
                pinned = inner.pinned
            };
        }

        [HttpPost("apps")]
        public IActionResult RegisterApplication(Mastodon.Application.Request request)
        {
            return Json(new Mastodon.Application.Response
            {
                Id = "1",
                ClientId = "id",
                ClientSecret = "secret"
            });
        }

        [HttpGet("accounts/{id}")]
        public async Task<IActionResult> GetAccount(string id)
        {
            id = Uri.UnescapeDataString(id);
            var user = await _entityStore.GetEntity(id, true);
            if (user == null) return NotFound();

            return Json(await _processAccount(user));
        }

        [HttpGet("statuses/{id}")]
        public async Task<IActionResult> GetStatus(string id)
        {
            CollectionTools.EntityCollectionItem item = null;
            if (int.TryParse(id, out var idInt))
            {
                item = await _collectionTools.GetCollectionItem(idInt);
            }
            else
            {
                var ent = await _entityStore.GetEntity(Uri.UnescapeDataString(id), true);
                if (ent != null) item = new CollectionTools.EntityCollectionItem { CollectionItemId = -1, Entity = ent };
            }

            if (item == null) return NotFound();
            var translated = await _translateStatus(item);
            if (translated == null) return NotFound();
            return Json(translated);
        }
    }
}