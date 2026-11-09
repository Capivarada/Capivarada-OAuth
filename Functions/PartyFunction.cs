using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>Server-authoritative party membership and invitations. Party state is private PlayFab internal data owned by its leader.</summary>
public sealed class PartyFunction : AuthenticatedFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly PlayFabServerClient playFab;

    public PartyFunction(PlayerSessionService sessions, PlayFabServerClient playFab) : base(sessions) => this.playFab = playFab;

    [Function("GetParty")]
    public async Task<HttpResponseData> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "party")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        try
        {
            PartyMembership membership = await Membership(session!.Value.PlayFabId);
            PartySummary? party = await Snapshot(membership);
            PartyInviteState invites = await Invites(session.Value.PlayFabId);
            return await Ok(request, new { party, incoming = await InviteSummaries(invites.Incoming, true), outgoing = await InviteSummaries(invites.Outgoing, false) });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("CreateParty")]
    public async Task<HttpResponseData> Create([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/create")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        try
        {
            PartyMembership existing = await Membership(session!.Value.PlayFabId);
            if (!string.IsNullOrWhiteSpace(existing.PartyId)) return await Bad(request, "Você já está em um grupo.");
            PartyRecord party = new PartyRecord { LeaderPlayerId = session.Value.PlayFabId };
            party.Members.Add(new PartyMemberRecord { PlayerId = session.Value.PlayFabId, Ready = true });
            await SaveParty(party);
            await SaveMembership(session.Value.PlayFabId, party);
            return await Ok(request, new { party = await Snapshot(party) });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("InviteToParty")]
    public async Task<HttpResponseData> Invite([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/invite")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        PartyInviteInput? input = await Read<PartyInviteInput>(request);
        if (string.IsNullOrWhiteSpace(input?.PlayerId)) return await Bad(request, "ID do amigo ausente.");
        try
        {
            string leaderId = session!.Value.PlayFabId;
            if (string.Equals(leaderId, input.PlayerId, StringComparison.Ordinal)) return await Bad(request, "Você não pode convidar a si mesmo.");
            PartyRecord party = await RequireLeaderParty(leaderId);
            if (party.Members.Count >= PartyStore.MaxMembers) return await Bad(request, "O grupo já está cheio.");
            if (!await playFab.AreFriendsAsync(leaderId, input.PlayerId)) return await Bad(request, "Apenas amigos podem ser convidados para o grupo.");
            if (!string.IsNullOrWhiteSpace((await Membership(input.PlayerId)).PartyId)) return await Bad(request, "Este jogador já está em outro grupo.");
            PartyInviteState own = await Invites(leaderId);
            if (own.Outgoing.Exists(invite => invite.TargetPlayerId == input.PlayerId && invite.PartyId == party.PartyId)) return await Bad(request, "Este amigo já recebeu um convite.");
            PartyInviteState target = await Invites(input.PlayerId);
            PartyInviteRecord invite = new PartyInviteRecord { PartyId = party.PartyId, LeaderPlayerId = leaderId, SenderPlayerId = leaderId, TargetPlayerId = input.PlayerId };
            own.Outgoing.Add(invite); target.Incoming.Add(invite);
            await SaveInvites(leaderId, own); await SaveInvites(input.PlayerId, target);
            return await Ok(request, new { inviteId = invite.InviteId });
        }
        catch (InvalidOperationException exception) { return await Bad(request, exception.Message); }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("AcceptPartyInvite")]
    public async Task<HttpResponseData> Accept([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/invitations/accept")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        PartyInviteActionInput? input = await Read<PartyInviteActionInput>(request);
        try
        {
            string playerId = session!.Value.PlayFabId;
            if (!string.IsNullOrWhiteSpace((await Membership(playerId)).PartyId)) return await Bad(request, "Você já está em um grupo.");
            PartyInviteState target = await Invites(playerId);
            PartyInviteRecord? invite = target.Incoming.Find(item => item.InviteId == input?.InviteId);
            if (invite == null) return await Bad(request, "Convite não encontrado ou expirado.");
            PartyRecord party = await Party(invite.LeaderPlayerId);
            if (party.PartyId != invite.PartyId || party.Members.Count >= PartyStore.MaxMembers) return await Bad(request, "Este grupo não está mais disponível.");
            party.Members.Add(new PartyMemberRecord { PlayerId = playerId, Ready = false });
            await SaveParty(party); await SaveMembership(playerId, party);
            PartyInviteState sender = await Invites(invite.SenderPlayerId);
            PartyStore.RemoveInvite(target, invite.InviteId); PartyStore.RemoveInvite(sender, invite.InviteId);
            await SaveInvites(playerId, target); await SaveInvites(invite.SenderPlayerId, sender);
            return await Ok(request, new { party = await Snapshot(party) });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("RejectPartyInvite")]
    public Task<HttpResponseData> Reject([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/invitations/reject")] HttpRequestData request) => RemoveInvite(request, false);
    [Function("CancelPartyInvite")]
    public Task<HttpResponseData> Cancel([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/invitations/cancel")] HttpRequestData request) => RemoveInvite(request, true);

    [Function("SetPartyReady")]
    public async Task<HttpResponseData> SetReady([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/ready")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        PartyReadyInput? input = await Read<PartyReadyInput>(request);
        try
        {
            PartyMembership membership = await Membership(session!.Value.PlayFabId);
            PartyRecord party = await Party(membership.LeaderPlayerId);
            PartyMemberRecord? member = PartyStore.FindMember(party, session.Value.PlayFabId);
            if (member == null) return await Bad(request, "Você não pertence a este grupo.");
            // The leader starts the search; readiness belongs to invited members.
            if (session.Value.PlayFabId != party.LeaderPlayerId) member.Ready = input?.Ready ?? false;
            await SaveParty(party);
            return await Ok(request, new { party = await Snapshot(party) });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("LeaveParty")]
    public async Task<HttpResponseData> Leave([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/leave")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        try { await RemoveMember(session!.Value.PlayFabId, session.Value.PlayFabId, false); return await Ok(request, new { }); }
        catch (InvalidOperationException exception) { return await Bad(request, exception.Message); }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("KickPartyMember")]
    public async Task<HttpResponseData> Kick([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "party/kick")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        PartyKickInput? input = await Read<PartyKickInput>(request);
        try
        {
            if (string.IsNullOrWhiteSpace(input?.PlayerId)) return await Bad(request, "Membro inválido.");
            await RemoveMember(session!.Value.PlayFabId, input.PlayerId, true);
            return await Ok(request, new { });
        }
        catch (InvalidOperationException exception) { return await Bad(request, exception.Message); }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    private async Task RemoveMember(string actorId, string targetId, bool mustBeLeader)
    {
        PartyMembership membership = await Membership(actorId);
        if (string.IsNullOrWhiteSpace(membership.PartyId)) throw new InvalidOperationException("Você não está em um grupo.");
        PartyRecord party = await Party(membership.LeaderPlayerId);
        if (mustBeLeader && party.LeaderPlayerId != actorId) throw new InvalidOperationException("Somente o líder pode remover membros.");
        if (!PartyStore.IsMember(party, targetId)) throw new InvalidOperationException("Membro não encontrado.");
        party.Members.RemoveAll(member => member.PlayerId == targetId);
        await playFab.RemoveInternalStateAsync(targetId, PartyStore.MembershipKey);
        if (party.Members.Count == 0) { await playFab.RemoveInternalStateAsync(party.LeaderPlayerId, PartyStore.PartyKey); return; }

        if (targetId == party.LeaderPlayerId)
        {
            PartyMemberRecord newLeader = party.Members.OrderBy(member => member.JoinedAtUtc).First();
            string oldLeader = party.LeaderPlayerId;
            party.LeaderPlayerId = newLeader.PlayerId;
            foreach (PartyMemberRecord member in party.Members) member.Ready = member.PlayerId == party.LeaderPlayerId;
            await playFab.RemoveInternalStateAsync(oldLeader, PartyStore.PartyKey);
        }
        await SaveParty(party);
        foreach (PartyMemberRecord member in party.Members) await SaveMembership(member.PlayerId, party);
    }

    private async Task<HttpResponseData> RemoveInvite(HttpRequestData request, bool outgoing)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        PartyInviteActionInput? input = await Read<PartyInviteActionInput>(request);
        try
        {
            PartyInviteState state = await Invites(session!.Value.PlayFabId);
            PartyInviteRecord? invite = (outgoing ? state.Outgoing : state.Incoming).Find(item => item.InviteId == input?.InviteId);
            if (invite == null) return await Bad(request, "Convite não encontrado.");
            string otherId = outgoing ? invite.TargetPlayerId : invite.SenderPlayerId;
            PartyInviteState other = await Invites(otherId);
            PartyStore.RemoveInvite(state, invite.InviteId); PartyStore.RemoveInvite(other, invite.InviteId);
            await SaveInvites(session.Value.PlayFabId, state); await SaveInvites(otherId, other);
            return await Ok(request, new { });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    private async Task<PartyRecord> RequireLeaderParty(string playerId)
    {
        PartyMembership membership = await Membership(playerId);
        if (string.IsNullOrWhiteSpace(membership.PartyId))
        {
            PartyRecord created = new PartyRecord { LeaderPlayerId = playerId };
            created.Members.Add(new PartyMemberRecord { PlayerId = playerId, Ready = true });
            await SaveParty(created); await SaveMembership(playerId, created);
            return created;
        }
        PartyRecord party = await Party(membership.LeaderPlayerId);
        if (party.LeaderPlayerId != playerId) throw new InvalidOperationException("Somente o líder pode convidar amigos.");
        return party;
    }

    private Task<PartyMembership> Membership(string playerId) => playFab.GetInternalStateAsync(playerId, PartyStore.MembershipKey, () => new PartyMembership());
    private Task<PartyInviteState> Invites(string playerId) => playFab.GetInternalStateAsync(playerId, PartyStore.InviteKey, () => new PartyInviteState());
    private async Task<PartyRecord> Party(string leaderId)
    {
        PartyRecord party = await playFab.GetInternalStateAsync(leaderId, PartyStore.PartyKey, () => new PartyRecord());
        if (string.IsNullOrWhiteSpace(party.PartyId) || party.Members.Count == 0) throw new InvalidOperationException("O grupo não existe mais.");
        return party;
    }
    private Task SaveParty(PartyRecord party) => playFab.SaveInternalStateAsync(party.LeaderPlayerId, PartyStore.PartyKey, party);
    private Task SaveMembership(string playerId, PartyRecord party) => playFab.SaveInternalStateAsync(playerId, PartyStore.MembershipKey, new PartyMembership { PartyId = party.PartyId, LeaderPlayerId = party.LeaderPlayerId });
    private Task SaveInvites(string playerId, PartyInviteState invites) => playFab.SaveInternalStateAsync(playerId, PartyStore.InviteKey, invites);

    private async Task<PartySummary?> Snapshot(PartyMembership membership)
    {
        if (string.IsNullOrWhiteSpace(membership.PartyId) || string.IsNullOrWhiteSpace(membership.LeaderPlayerId)) return null;
        PartyRecord party = await Party(membership.LeaderPlayerId);
        return await Snapshot(party);
    }
    private async Task<PartySummary> Snapshot(PartyRecord party)
    {
        List<PartyMemberSummary> members = new();
        foreach (PartyMemberRecord member in party.Members)
        {
            FriendSummary profile = await playFab.GetSummaryAsync(member.PlayerId);
            members.Add(new PartyMemberSummary { PlayerId = member.PlayerId, Nickname = profile.Nickname, AvatarId = profile.AvatarId, Level = profile.Level, Ready = member.Ready, IsLeader = member.PlayerId == party.LeaderPlayerId });
        }
        return new PartySummary { PartyId = party.PartyId, LeaderPlayerId = party.LeaderPlayerId, Members = members.ToArray() };
    }
    private async Task<List<PartyInviteSummary>> InviteSummaries(IEnumerable<PartyInviteRecord> records, bool incoming)
    {
        List<PartyInviteSummary> result = new();
        foreach (PartyInviteRecord invite in records)
        {
            FriendSummary profile = await playFab.GetSummaryAsync(incoming ? invite.SenderPlayerId : invite.TargetPlayerId);
            result.Add(new PartyInviteSummary { InviteId = invite.InviteId, PartyId = invite.PartyId, SenderPlayerId = invite.SenderPlayerId, TargetPlayerId = invite.TargetPlayerId, Nickname = profile.Nickname, AvatarId = profile.AvatarId, Level = profile.Level, CreatedAtUtc = invite.CreatedAtUtc });
        }
        return result;
    }
    private static Task<T?> Read<T>(HttpRequestData request) => JsonSerializer.DeserializeAsync<T>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).AsTask();
    private static async Task<HttpResponseData> Ok(HttpRequestData request, object value) { HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(value, Json)); return response; }
    private static async Task<HttpResponseData> Bad(HttpRequestData request, string message) { HttpResponseData response = request.CreateResponse(HttpStatusCode.BadRequest); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json)); return response; }
    private static async Task<HttpResponseData> Fail(HttpRequestData request, Exception exception) { Console.Error.WriteLine(exception); HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = "Falha ao processar o grupo." }, Json)); return response; }
}
