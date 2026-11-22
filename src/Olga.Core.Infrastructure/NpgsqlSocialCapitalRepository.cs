using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Infrastructure;

public sealed class NpgsqlSocialCapitalRepository(NpgsqlDataSource dataSource, IIdentityProtector identityProtector) : ISocialCapitalRepository
{
    public async Task<VentureResponse> CreateVentureAsync(string ownerMemberId, VentureCreateRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var actor = new NpgsqlCommand("SELECT set_config('app.actor_id', @actor, true)", connection, transaction))
        {
            Add(actor, "actor", ownerMemberId); await actor.ExecuteNonQueryAsync(ct);
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO core.venture (venture_id, owner_member_id, organization_id, name, industry_code, stage_code, revenue_band_code, team_size_band_code)
            VALUES (@venture_id, @owner_member_id, @organization_id, @name, @industry_code, @stage_code, @revenue_band_code, @team_size_band_code)
            ON CONFLICT (venture_id) DO NOTHING
            """, connection, transaction))
        {
            Add(command, "venture_id", request.VentureId); Add(command, "owner_member_id", ownerMemberId); Add(command, "organization_id", request.OrganizationId);
            Add(command, "name", request.Name.Trim()); Add(command, "industry_code", request.IndustryCode); Add(command, "stage_code", request.StageCode);
            Add(command, "revenue_band_code", request.RevenueBandCode); Add(command, "team_size_band_code", request.TeamSizeBandCode);
            await command.ExecuteNonQueryAsync(ct);
        }
        var geographies = (request.GeographyCodes ?? []).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var geography in geographies)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO core.venture_geography (venture_pk, geography_code, is_primary)
                SELECT venture_pk, @geography_code, @is_primary FROM core.venture
                WHERE venture_id = @venture_id AND owner_member_id = @owner_member_id
                ON CONFLICT (venture_pk, geography_code) DO NOTHING
                """, connection, transaction);
            Add(command, "geography_code", geography); command.Parameters.AddWithValue("is_primary", geographies[0] == geography);
            Add(command, "venture_id", request.VentureId); Add(command, "owner_member_id", ownerMemberId);
            await command.ExecuteNonQueryAsync(ct);
        }
        var result = await ReadVentureAsync(connection, transaction, ownerMemberId, request.VentureId, ct)
            ?? throw new DomainException("VENTURE_ID_CONFLICT", 409);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<VentureResponse>> ListVenturesAsync(string ownerMemberId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT v.venture_id, v.name, v.organization_id, v.industry_code, v.stage_code,
                   v.revenue_band_code, v.team_size_band_code, v.status, v.updated_at, v.row_version,
                   COALESCE(array_agg(g.geography_code ORDER BY g.is_primary DESC, g.geography_code)
                       FILTER (WHERE g.geography_code IS NOT NULL), ARRAY[]::varchar[])
            FROM core.venture v
            LEFT JOIN core.venture_geography g ON g.venture_pk=v.venture_pk
            WHERE v.owner_member_id=@owner_member_id
            GROUP BY v.venture_pk
            ORDER BY v.updated_at DESC, v.venture_pk DESC
            """);
        Add(command, "owner_member_id", ownerMemberId);
        var ventures = new List<VentureResponse>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ventures.Add(ReadVenture(reader));
        return ventures;
    }

    public async Task<VentureResponse?> GetVentureAsync(string ownerMemberId, string ventureId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadVentureAsync(connection, null, ownerMemberId, ventureId, ct);
    }

    public async Task<ContactImportResponse> ImportContactsAsync(string ownerMemberId, IReadOnlyList<ProtectedContactImport> contacts, int retentionDays, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var importedLinkIds = new List<string>(contacts.Count);
        foreach (var item in contacts)
        {
            await using (var suppression = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM social.contact_suppression
                    WHERE hash_key_version='identity-v1'
                      AND ((identifier_type='EMAIL' AND identifier_hash=@email_hash)
                        OR (identifier_type='PHONE' AND identifier_hash=@phone_hash)))
                """, connection, transaction))
            {
                suppression.Parameters.Add("email_hash", NpgsqlDbType.Char).Value = item.EmailHash is null ? DBNull.Value : item.EmailHash;
                suppression.Parameters.Add("phone_hash", NpgsqlDbType.Char).Value = item.PhoneHash is null ? DBNull.Value : item.PhoneHash;
                if (Convert.ToBoolean(await suppression.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)) continue;
            }
            long recordPk;
            await using (var command = new NpgsqlCommand("""
                INSERT INTO social.contact_record (
                    contact_record_id, owner_member_id, label_ciphertext, job_title_ciphertext,
                    company_ciphertext, industry_ciphertext, city_ciphertext, source_type, expires_at)
                VALUES (
                    @contact_record_id, @owner_member_id, @label_ciphertext, @job_title_ciphertext,
                    @company_ciphertext, @industry_ciphertext, @city_ciphertext, 'PHONE',
                    CURRENT_TIMESTAMP + make_interval(days => @retention_days))
                ON CONFLICT (contact_record_id) DO UPDATE SET
                    label_ciphertext = EXCLUDED.label_ciphertext,
                    job_title_ciphertext = EXCLUDED.job_title_ciphertext,
                    company_ciphertext = EXCLUDED.company_ciphertext,
                    industry_ciphertext = EXCLUDED.industry_ciphertext,
                    city_ciphertext = EXCLUDED.city_ciphertext,
                    last_refreshed_at = CURRENT_TIMESTAMP,
                    expires_at = EXCLUDED.expires_at
                WHERE social.contact_record.owner_member_id = EXCLUDED.owner_member_id
                RETURNING contact_record_pk
                """, connection, transaction))
            {
                Add(command, "contact_record_id", item.ContactRecordId); Add(command, "owner_member_id", ownerMemberId);
                command.Parameters.Add("label_ciphertext", NpgsqlDbType.Bytea).Value = item.LabelCiphertext;
                AddNullableBytea(command, "job_title_ciphertext", item.JobTitleCiphertext);
                AddNullableBytea(command, "company_ciphertext", item.CompanyCiphertext);
                AddNullableBytea(command, "industry_ciphertext", item.IndustryCiphertext);
                AddNullableBytea(command, "city_ciphertext", item.CityCiphertext);
                command.Parameters.AddWithValue("retention_days", retentionDays);
                recordPk = Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? throw new DomainException("CONTACT_ID_CONFLICT", 409), CultureInfo.InvariantCulture);
            }
            await UpsertIdentifierAsync(connection, transaction, recordPk, ownerMemberId, "EMAIL", item.EmailHash, ct);
            await UpsertIdentifierAsync(connection, transaction, recordPk, ownerMemberId, "PHONE", item.PhoneHash, ct);
            await using var link = new NpgsqlCommand("""
                INSERT INTO social.contact_link (link_id, owner_member_id, contact_record_pk, origin_type, lifecycle_state)
                VALUES (@link_id, @owner_member_id, @contact_record_pk, 'IMPORT', 'ESTIMATED')
                ON CONFLICT (link_id) DO NOTHING
                """, connection, transaction);
            Add(link, "link_id", item.LinkId); Add(link, "owner_member_id", ownerMemberId); link.Parameters.AddWithValue("contact_record_pk", recordPk);
            if (await link.ExecuteNonQueryAsync(ct) == 0)
            {
                await using var ownerCheck = new NpgsqlCommand("SELECT count(*) FROM social.contact_link WHERE link_id=@link_id AND owner_member_id=@owner_member_id AND contact_record_pk=@contact_record_pk", connection, transaction);
                Add(ownerCheck, "link_id", item.LinkId); Add(ownerCheck, "owner_member_id", ownerMemberId); ownerCheck.Parameters.AddWithValue("contact_record_pk", recordPk);
                if (Convert.ToInt32(await ownerCheck.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1) throw new DomainException("CONTACT_LINK_ID_CONFLICT", 409);
            }
            importedLinkIds.Add(item.LinkId);
        }
        await transaction.CommitAsync(ct);
        return new(importedLinkIds.Count, importedLinkIds);
    }

    public async Task<InviteBatchResponse> CreateInviteBatchAsync(string ownerMemberId, InviteBatchCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays);
        await using (var batch = new NpgsqlCommand("""
            INSERT INTO social.invite_batch (batch_id, owner_member_id, venture_pk, status, approved_at, expires_at)
            SELECT @batch_id, @owner_member_id, v.venture_pk, 'APPROVED', CURRENT_TIMESTAMP, @expires_at
            FROM (SELECT 1) seed
            LEFT JOIN core.venture v ON v.venture_id=@venture_id AND v.owner_member_id=@owner_member_id AND v.status='ACTIVE'
            WHERE @venture_id IS NULL OR v.venture_pk IS NOT NULL
            ON CONFLICT (batch_id) DO NOTHING
            """, connection, transaction))
        {
            Add(batch, "batch_id", request.BatchId); Add(batch, "owner_member_id", ownerMemberId); Add(batch, "venture_id", request.VentureId);
            batch.Parameters.AddWithValue("expires_at", expiresAt); await batch.ExecuteNonQueryAsync(ct);
        }
        var invitations = new List<InvitationDispatch>();
        for (var index = 0; index < request.LinkIds.Count; index++)
        {
            var token = identityProtector.CreateInvitationToken(ownerMemberId, request.BatchId, request.LinkIds[index], idempotencyKey);
            var invitationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"INVITATION_ID\n{token}")))[..16].ToLowerInvariant();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            await using var command = new NpgsqlCommand("""
                INSERT INTO social.invitation (invitation_id, invite_batch_pk, contact_link_pk, owner_member_id, batch_position, token_hash, expires_at)
                SELECT @invitation_id, b.invite_batch_pk, l.contact_link_pk, @owner_member_id, @batch_position, @token_hash, @expires_at
                FROM social.invite_batch b
                JOIN social.contact_link l ON l.link_id=@link_id AND l.owner_member_id=@owner_member_id
                WHERE b.batch_id=@batch_id AND b.owner_member_id=@owner_member_id
                ON CONFLICT (invite_batch_pk, contact_link_pk) DO NOTHING
                """, connection, transaction);
            Add(command, "invitation_id", invitationId); Add(command, "owner_member_id", ownerMemberId); command.Parameters.AddWithValue("batch_position", (short)(index + 1));
            Add(command, "token_hash", tokenHash); command.Parameters.AddWithValue("expires_at", expiresAt); Add(command, "link_id", request.LinkIds[index]); Add(command, "batch_id", request.BatchId);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
            {
                await using var replay = new NpgsqlCommand("""
                    SELECT count(*) FROM social.invitation i
                    JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
                    JOIN social.contact_link l ON l.contact_link_pk=i.contact_link_pk
                    WHERE b.batch_id=@batch_id AND b.owner_member_id=@owner_member_id
                      AND l.link_id=@link_id AND i.token_hash=@token_hash AND i.status='APPROVED'
                    """, connection, transaction);
                Add(replay, "batch_id", request.BatchId); Add(replay, "owner_member_id", ownerMemberId); Add(replay, "link_id", request.LinkIds[index]); Add(replay, "token_hash", tokenHash);
                if (Convert.ToInt32(await replay.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1) throw new DomainException("INVITE_BATCH_LINK_INVALID", 409);
            }
            await using (var markInvited = new NpgsqlCommand("""
                UPDATE social.contact_link
                SET lifecycle_state='INVITED', last_transition_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP
                WHERE link_id=@link_id AND owner_member_id=@owner_member_id AND lifecycle_state='ESTIMATED'
                """, connection, transaction))
            {
                Add(markInvited, "link_id", request.LinkIds[index]);
                Add(markInvited, "owner_member_id", ownerMemberId);
                await markInvited.ExecuteNonQueryAsync(ct);
            }
            invitations.Add(new(invitationId, request.LinkIds[index], token, expiresAt));
        }
        await transaction.CommitAsync(ct);
        return new(request.BatchId, "APPROVED", invitations, "\"1\"");
    }

    public Task<InvitationAcceptResponse> AcceptInvitationAsync(string recipientMemberId, string token, string idempotencyKey, CancellationToken ct) =>
        AcceptAsync(recipientMemberId, "i.token_hash=@lookup", TokenHash(token), idempotencyKey, ct);

    // Accepts an invitation addressed to one of the member's verified phone/email identities,
    // without the shared token (the "pending invites" inbox path).
    public Task<InvitationAcceptResponse> AcceptPendingInvitationAsync(string recipientMemberId, string invitationId, string idempotencyKey, CancellationToken ct) =>
        AcceptAsync(recipientMemberId, $"i.invitation_id=@lookup AND {MatchesRecipientIdentity}", invitationId, idempotencyKey, ct);

    private async Task<InvitationAcceptResponse> AcceptAsync(string recipientMemberId, string lookupFilter, string lookup, string idempotencyKey, CancellationToken ct)
    {
        _ = idempotencyKey; // Validated by the application service; the invitation-derived request ID makes retries stable.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string invitationId, senderMemberId, linkId;
        await using (var read = new NpgsqlCommand($"""
            SELECT i.invitation_id, i.owner_member_id, l.link_id
            FROM social.invitation i
            JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
            JOIN social.contact_link l ON l.contact_link_pk=i.contact_link_pk
            WHERE {lookupFilter} AND i.status IN ('APPROVED','OPENED','ACCEPTED')
              AND i.expires_at>CURRENT_TIMESTAMP AND b.expires_at>CURRENT_TIMESTAMP
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id)
            FOR UPDATE OF i,l
            """, connection, transaction))
        {
            Add(read, "lookup", lookup); Add(read, "recipient_member_id", recipientMemberId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new DomainException("INVITATION_NOT_FOUND", 404);
            invitationId=reader.GetString(0); senderMemberId=reader.GetString(1); linkId=reader.GetString(2);
        }
        if (senderMemberId == recipientMemberId) throw new DomainException("INVITATION_SELF_ACCEPT_INVALID", 409);
        var requestId = StableId("ireq", $"{invitationId}:{recipientMemberId}");
        await using (var request = new NpgsqlCommand("""
            INSERT INTO social.connection_request (connection_request_id,sender_member_id,recipient_member_id,context_id,status,expires_at)
            VALUES (@request_id,@sender_member_id,@recipient_member_id,'SCI_INVITATION','PENDING',CURRENT_TIMESTAMP + interval '1 day')
            ON CONFLICT (connection_request_id) DO NOTHING
            """, connection, transaction))
        { Add(request,"request_id",requestId); Add(request,"sender_member_id",recipientMemberId); Add(request,"recipient_member_id",senderMemberId); await request.ExecuteNonQueryAsync(ct); }
        await using (var update = new NpgsqlCommand("""
            UPDATE social.invitation i SET status='ACCEPTED', accepted_at=COALESCE(accepted_at,CURRENT_TIMESTAMP), updated_at=CURRENT_TIMESTAMP
            FROM social.contact_link l
            WHERE i.invitation_id=@invitation_id AND l.contact_link_pk=i.contact_link_pk
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id);
            UPDATE social.contact_link l SET contact_member_id=@recipient_member_id,
              lifecycle_state='JOINED', joined_at=COALESCE(joined_at,CURRENT_TIMESTAMP),
              last_transition_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP
            FROM social.invitation i WHERE i.invitation_id=@invitation_id AND i.contact_link_pk=l.contact_link_pk
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id)
              AND l.lifecycle_state IN ('INVITED','JOINED','REQUEST_PENDING');
            UPDATE social.contact_link l SET lifecycle_state='REQUEST_PENDING',
              last_transition_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP
            FROM social.invitation i WHERE i.invitation_id=@invitation_id AND i.contact_link_pk=l.contact_link_pk
              AND l.contact_member_id=@recipient_member_id AND l.lifecycle_state='JOINED';
            """, connection, transaction))
        { Add(update,"invitation_id",invitationId); Add(update,"recipient_member_id",recipientMemberId); await update.ExecuteNonQueryAsync(ct); }
        await transaction.CommitAsync(ct);
        return new(invitationId,requestId,"REQUEST_PENDING",linkId);
    }

    public async Task<SciContactPage> GetSciAsync(string ownerMemberId, string? cursor, int limit, CancellationToken ct)
    {
        var (afterScore, afterProjectionId) = DecodeSciCursor(cursor);
        await using var command = dataSource.CreateCommand("""
            SELECT projection_id, link_id, contact_member_id, contact_record_id, label_ciphertext,
                   lifecycle_state, match_score, band, is_estimated, counts_in_sci, counts_in_potential,
                   projection_state, frozen_at, freeze_reason, layer_code, layer_ordinal, outcome_points,
                   tie_strength, reason_code, scorer_version, scoring_config_id, score_calculated_at,
                   last_activity_at, projection_version, as_of, row_version
            FROM social.vw_sci_contact_v2
            WHERE owner_member_id=@owner_member_id
              AND (@after_score IS NULL OR (COALESCE(match_score,-1), projection_id) < (@after_score, @after_projection_id))
            ORDER BY COALESCE(match_score,-1) DESC, projection_id DESC
            LIMIT @take
            """);
        Add(command, "owner_member_id", ownerMemberId);
        command.Parameters.Add("after_score", NpgsqlDbType.Integer).Value = afterScore is null ? DBNull.Value : afterScore.Value;
        Add(command, "after_projection_id", afterProjectionId);
        command.Parameters.AddWithValue("take", limit + 1);
        var rows = new List<(string ProjectionId, int ScoreKey, SciContactResponse Value)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var recordId = reader.GetString(3);
            var score = reader.IsDBNull(6) ? (int?)null : reader.GetInt16(6);
            rows.Add((reader.GetString(0), score ?? -1, new(
                reader.GetString(1), NullableString(reader, 2),
                identityProtector.UnprotectContactLabel(ownerMemberId, recordId, reader.GetFieldValue<byte[]>(4)),
                reader.GetString(5), score, reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetBoolean(10),
                reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12), NullableString(reader, 13),
                NullableString(reader, 14), reader.IsDBNull(15) ? null : reader.GetInt16(15), reader.GetDecimal(16), reader.GetDecimal(17),
                NullableString(reader, 18), NullableString(reader, 19), NullableString(reader, 20),
                reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21), reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
                reader.GetInt64(23), reader.GetFieldValue<DateTimeOffset>(24), $"\"{reader.GetInt64(25):x}\"")));
        }
        var page = rows.Take(limit).ToArray();
        return new(page.Select(x => x.Value).ToArray(), rows.Count > limit ? EncodeSciCursor(page[^1].ScoreKey, page[^1].ProjectionId) : null);
    }

    public async Task<SciSummaryResponse> GetSciSummaryAsync(string ownerMemberId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        string? goalId = null; int sciCount = 0, goalValue = 0, goalSequence = 0; string? goalOwnerType = null;
        DateTimeOffset? goalTargetAt = null;
        SciSummaryResponse summary;
        await using (var command = new NpgsqlCommand("SELECT * FROM social.vw_sci_summary_v2 WHERE owner_member_id=@owner_member_id", connection))
        {
            Add(command, "owner_member_id", ownerMemberId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new(0, 0, 0, 0, 0, 0, 0, 0, "RECALCULATING", 0, null, null, DateTimeOffset.UtcNow, null, "\"0\"");
            sciCount = reader.GetInt32(1); goalId = NullableString(reader, 9);
            goalValue = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            goalSequence = reader.IsDBNull(11) ? 0 : reader.GetInt32(11);
            goalOwnerType = NullableString(reader, 13);
            goalTargetAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14);
            summary = new(sciCount, reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetInt32(7), reader.GetInt32(8), reader.GetString(16), reader.GetInt64(17), NullableString(reader, 18),
                NullableString(reader, 19), reader.GetFieldValue<DateTimeOffset>(20), null, $"\"{reader.GetInt64(21):x}\"");
        }
        if (goalId is null) return summary;
        var milestones = new List<SciGoalMilestoneResponse>();
        await using (var command = new NpgsqlCommand("""
            SELECT m.milestone_percent, m.reached_at
            FROM social.sci_goal_milestone m JOIN social.sci_goal g ON g.sci_goal_pk=m.sci_goal_pk
            WHERE g.goal_id=@goal_id ORDER BY m.milestone_percent
            """, connection))
        {
            Add(command, "goal_id", goalId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) milestones.Add(new(reader.GetInt16(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }
        var progress = goalValue <= 0 ? 0 : Math.Min(100, (int)((long)sciCount * 100 / goalValue));
        return summary with { Goal = new(goalId, goalValue, goalSequence, goalOwnerType!, progress, goalTargetAt, milestones) };
    }

    public async Task<ImportedContactPage> ListContactsAsync(string ownerMemberId, string? cursor, int limit, CancellationToken ct)
    {
        var afterPk = DecodePkCursor(cursor, "CONTACT_CURSOR_INVALID");
        await using var command = dataSource.CreateCommand($"""
            SELECT l.contact_link_pk, l.link_id, r.contact_record_id, r.label_ciphertext, l.lifecycle_state, l.contact_member_id,
                   EXISTS (SELECT 1 FROM social.contact_identifier ci WHERE ci.contact_record_pk=r.contact_record_pk AND ci.identifier_type='EMAIL'),
                   EXISTS (SELECT 1 FROM social.contact_identifier ci WHERE ci.contact_record_pk=r.contact_record_pk AND ci.identifier_type='PHONE'),
                   inv.invitation_id, {EffectiveInvitationStatus("inv")}, inv.effective_expires_at, l.created_at, r.expires_at
            FROM social.contact_link l
            JOIN social.contact_record r ON r.contact_record_pk=l.contact_record_pk
            LEFT JOIN LATERAL (
                SELECT i.invitation_id, i.status, LEAST(i.expires_at, b.expires_at) AS effective_expires_at
                FROM social.invitation i JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
                WHERE i.contact_link_pk=l.contact_link_pk
                ORDER BY i.created_at DESC, i.invitation_pk DESC LIMIT 1) inv ON true
            WHERE l.owner_member_id=@owner_member_id AND l.lifecycle_state<>'REMOVED'
              AND r.status='ACTIVE' AND r.expires_at>CURRENT_TIMESTAMP
              AND (@after_pk = 0 OR l.contact_link_pk < @after_pk)
            ORDER BY l.contact_link_pk DESC
            LIMIT @take
            """);
        Add(command, "owner_member_id", ownerMemberId);
        command.Parameters.AddWithValue("after_pk", afterPk); command.Parameters.AddWithValue("take", limit + 1);
        var rows = new List<(long Pk, ImportedContactResponse Value)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var recordId = reader.GetString(2);
            rows.Add((reader.GetInt64(0), new(reader.GetString(1), recordId,
                identityProtector.UnprotectContactLabel(ownerMemberId, recordId, reader.GetFieldValue<byte[]>(3)),
                reader.GetString(4), NullableString(reader, 5), reader.GetBoolean(6), reader.GetBoolean(7),
                NullableString(reader, 8), NullableString(reader, 9), reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12))));
        }
        var page = rows.Take(limit).ToArray();
        return new(page.Select(x => x.Value).ToArray(), rows.Count > limit ? EncodePkCursor(page[^1].Pk) : null);
    }

    public async Task<SciReachOutPage> GetReachOutAsync(string ownerMemberId, string? cursor, int limit, CancellationToken ct)
    {
        var afterRank = DecodePkCursor(cursor, "SCI_REACH_OUT_CURSOR_INVALID");
        await using var command = dataSource.CreateCommand("""
            SELECT recommendation.recommendation_id, link.link_id, link.contact_member_id,
                   recommendation.reason_code, recommendation.match_score, recommendation.relevance_score,
                   recommendation.rank, recommendation.projection_version, recommendation.generated_at,
                   recommendation.expires_at
            FROM social.sci_reach_out_projection recommendation
            JOIN social.sci_member_state state ON state.owner_member_id=recommendation.owner_member_id
              AND state.current_projection_version=recommendation.projection_version
            JOIN social.contact_link link ON link.contact_link_pk=recommendation.contact_link_pk
              AND link.owner_member_id=recommendation.owner_member_id
            WHERE recommendation.owner_member_id=@owner_member_id
              AND recommendation.status='ACTIVE' AND recommendation.expires_at>CURRENT_TIMESTAMP
              AND (@after_rank=0 OR recommendation.rank>@after_rank)
            ORDER BY recommendation.rank
            LIMIT @take
            """);
        Add(command, "owner_member_id", ownerMemberId);
        command.Parameters.AddWithValue("after_rank", afterRank);
        command.Parameters.AddWithValue("take", limit + 1);
        var rows = new List<SciReachOutResponse>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt16(4), reader.GetDecimal(5), reader.GetInt32(6), reader.GetInt64(7),
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9)));
        var page = rows.Take(limit).ToArray();
        return new(page, rows.Count > limit ? EncodePkCursor(page[^1].Rank) : null);
    }

    public async Task<RelationshipOutcomeResponse> CreateOutcomeAsync(string memberId, RelationshipOutcomeCreateRequest request, string idempotencyKey, CancellationToken ct)
    {
        _ = idempotencyKey; // The API idempotency middleware owns request replay; outcome_id is also unique.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO social.relationship_outcome (
                outcome_id, outcome_type, status, initiated_by_member_id,
                occurred_at, confirmation_due_at, result_type)
            SELECT @outcome_id, @outcome_type, 'PENDING', @member_id, @occurred_at,
                   @occurred_at + CASE @outcome_type
                       WHEN 'MEETING' THEN interval '7 days'
                       WHEN 'INTRODUCTION' THEN interval '14 days'
                       ELSE interval '30 days' END,
                   @result_type
            WHERE EXISTS (
                SELECT 1 FROM social.contact_link link
                JOIN social.connection connection ON connection.connection_id=link.connection_id AND connection.status='ACTIVE'
                WHERE link.owner_member_id=@member_id AND link.contact_member_id=@contact_member_id
                  AND link.lifecycle_state='CONNECTED')
              AND NOT EXISTS (
                SELECT 1 FROM social.member_block block WHERE block.removed_at IS NULL
                  AND ((block.blocker_member_id=@member_id AND block.blocked_member_id=@contact_member_id)
                    OR (block.blocker_member_id=@contact_member_id AND block.blocked_member_id=@member_id)))
            ON CONFLICT (outcome_id) DO NOTHING
            """, connection, transaction))
        {
            Add(command, "outcome_id", request.OutcomeId); Add(command, "outcome_type", request.OutcomeType);
            Add(command, "member_id", memberId); Add(command, "contact_member_id", request.ContactMemberId);
            Add(command, "result_type", request.ResultType);
            command.Parameters.AddWithValue("occurred_at", request.OccurredAt);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var parties = new NpgsqlCommand("""
            INSERT INTO social.relationship_outcome_party (relationship_outcome_pk, member_id, party_role)
            SELECT outcome.relationship_outcome_pk, party.member_id, party.party_role
            FROM social.relationship_outcome outcome
            CROSS JOIN (VALUES (@member_id::varchar,'INITIATOR'::varchar),(@contact_member_id::varchar,'PARTICIPANT'::varchar)) party(member_id,party_role)
            WHERE outcome.outcome_id=@outcome_id AND outcome.initiated_by_member_id=@member_id
            ON CONFLICT (relationship_outcome_pk, member_id, party_role) DO NOTHING
            """, connection, transaction))
        {
            Add(parties, "outcome_id", request.OutcomeId); Add(parties, "member_id", memberId); Add(parties, "contact_member_id", request.ContactMemberId);
            await parties.ExecuteNonQueryAsync(ct);
        }
        if (request.OutcomeType == "INTRODUCTION")
        {
            await using var notify = new NpgsqlCommand("""
                INSERT INTO ops.outbox_event (outbox_event_id,aggregate_type,aggregate_id,event_type,payload_json)
                VALUES ('out_'||substr(md5(@outcome_id||':introduction'),1,32),'RELATIONSHIP_OUTCOME',@outcome_id,
                        'RelationshipIntroductionRequested.v1',
                        jsonb_build_object('outcome_id',@outcome_id,'sender_member_id',@member_id,'recipient_member_id',@contact_member_id))
                ON CONFLICT (outbox_event_id) DO NOTHING
                """, connection, transaction);
            Add(notify,"outcome_id",request.OutcomeId); Add(notify,"member_id",memberId); Add(notify,"contact_member_id",request.ContactMemberId);
            await notify.ExecuteNonQueryAsync(ct);
        }
        var response = await ReadOutcomeAsync(connection, transaction, request.OutcomeId, memberId, ct)
            ?? throw new DomainException("RELATIONSHIP_OUTCOME_NOT_ALLOWED", 409);
        if (response.ContactMemberId != request.ContactMemberId || response.OutcomeType != request.OutcomeType || response.OccurredAt != request.OccurredAt)
            throw new DomainException("IDEMPOTENCY_KEY_REUSED", 409);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<RelationshipOutcomeResponse> DecideOutcomeAsync(string memberId, string outcomeId, RelationshipOutcomeDecisionRequest request, string idempotencyKey, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string status; DateTimeOffset due; DateTimeOffset? withdrawalDue;
        await using (var read = new NpgsqlCommand("""
            SELECT outcome.status, outcome.confirmation_due_at, outcome.withdrawal_due_at
            FROM social.relationship_outcome outcome
            WHERE outcome.outcome_id=@outcome_id
              AND EXISTS (SELECT 1 FROM social.relationship_outcome_party party
                          WHERE party.relationship_outcome_pk=outcome.relationship_outcome_pk AND party.member_id=@member_id)
            FOR UPDATE
            """, connection, transaction))
        {
            Add(read, "outcome_id", outcomeId); Add(read, "member_id", memberId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new DomainException("RELATIONSHIP_OUTCOME_NOT_FOUND", 404);
            status=reader.GetString(0); due=reader.GetFieldValue<DateTimeOffset>(1); withdrawalDue=reader.IsDBNull(2)?null:reader.GetFieldValue<DateTimeOffset>(2);
        }
        await using (var replay = new NpgsqlCommand("""
            SELECT decision FROM social.relationship_outcome_confirmation
            WHERE actor_type='MEMBER' AND actor_member_id=@member_id AND idempotency_key=@idempotency_key
            """, connection, transaction))
        {
            Add(replay, "member_id", memberId); Add(replay, "idempotency_key", idempotencyKey);
            var prior = (string?)await replay.ExecuteScalarAsync(ct);
            if (prior is not null && prior != request.Decision) throw new DomainException("IDEMPOTENCY_KEY_REUSED", 409);
            if (prior is null)
            {
                if (request.Decision is "CONFIRM" or "DENY" && (status != "PENDING" || due < DateTimeOffset.UtcNow))
                    throw new DomainException("RELATIONSHIP_OUTCOME_NOT_PENDING", 409);
                if (request.Decision == "WITHDRAW" && (status != "VERIFIED" || withdrawalDue is null || withdrawalDue < DateTimeOffset.UtcNow))
                    throw new DomainException("RELATIONSHIP_OUTCOME_WITHDRAWAL_CLOSED", 409);
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO social.relationship_outcome_confirmation (
                        confirmation_id, relationship_outcome_pk, actor_member_id,
                        actor_type, decision, idempotency_key)
                    SELECT @confirmation_id, relationship_outcome_pk, @member_id, 'MEMBER', @decision, @idempotency_key
                    FROM social.relationship_outcome WHERE outcome_id=@outcome_id
                    """, connection, transaction);
                Add(insert, "confirmation_id", StableId("confirm", $"{memberId}:{idempotencyKey}"));
                Add(insert, "outcome_id", outcomeId); Add(insert, "member_id", memberId);
                Add(insert, "decision", request.Decision); Add(insert, "idempotency_key", idempotencyKey);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        await using (var decide = new NpgsqlCommand("""
            UPDATE social.relationship_outcome outcome
            SET status=CASE
                    WHEN @decision='DENY' THEN 'DENIED'
                    WHEN @decision='WITHDRAW' THEN 'WITHDRAWN'
                    WHEN 2 <= (SELECT count(DISTINCT confirmation.actor_member_id)
                               FROM social.relationship_outcome_confirmation confirmation
                               WHERE confirmation.relationship_outcome_pk=outcome.relationship_outcome_pk
                                 AND confirmation.actor_type='MEMBER' AND confirmation.decision='CONFIRM') THEN 'VERIFIED'
                    ELSE status END,
                verified_at=CASE WHEN @decision='CONFIRM' AND 2 <= (
                    SELECT count(DISTINCT confirmation.actor_member_id)
                    FROM social.relationship_outcome_confirmation confirmation
                    WHERE confirmation.relationship_outcome_pk=outcome.relationship_outcome_pk
                      AND confirmation.actor_type='MEMBER' AND confirmation.decision='CONFIRM')
                    THEN COALESCE(verified_at,CURRENT_TIMESTAMP) ELSE verified_at END,
                withdrawal_due_at=CASE WHEN @decision='CONFIRM' AND 2 <= (
                    SELECT count(DISTINCT confirmation.actor_member_id)
                    FROM social.relationship_outcome_confirmation confirmation
                    WHERE confirmation.relationship_outcome_pk=outcome.relationship_outcome_pk
                      AND confirmation.actor_type='MEMBER' AND confirmation.decision='CONFIRM')
                    THEN COALESCE(withdrawal_due_at,CURRENT_TIMESTAMP + interval '30 days') ELSE withdrawal_due_at END,
                valid_until=CASE WHEN @decision='CONFIRM' AND 2 <= (
                    SELECT count(DISTINCT confirmation.actor_member_id)
                    FROM social.relationship_outcome_confirmation confirmation
                    WHERE confirmation.relationship_outcome_pk=outcome.relationship_outcome_pk
                      AND confirmation.actor_type='MEMBER' AND confirmation.decision='CONFIRM')
                    THEN COALESCE(valid_until,CURRENT_TIMESTAMP + interval '12 months') ELSE valid_until END,
                updated_at=CURRENT_TIMESTAMP
            WHERE outcome.outcome_id=@outcome_id;

            UPDATE social.relationship_outcome_party party
            SET points_awarded=CASE WHEN outcome.status='VERIFIED' THEN
                    CASE outcome.outcome_type WHEN 'MEETING' THEN 1 WHEN 'INTRODUCTION' THEN 2 ELSE 3 END
                ELSE 0 END
            FROM social.relationship_outcome outcome
            WHERE outcome.relationship_outcome_pk=party.relationship_outcome_pk AND outcome.outcome_id=@outcome_id
            """, connection, transaction))
        {
            Add(decide, "decision", request.Decision); Add(decide, "outcome_id", outcomeId);
            await decide.ExecuteNonQueryAsync(ct);
        }
        await ReprojectRelationshipsAsync(connection, transaction, outcomeId, ct);
        var response = await ReadOutcomeAsync(connection, transaction, outcomeId, memberId, ct)
            ?? throw new DomainException("RELATIONSHIP_OUTCOME_NOT_FOUND", 404);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<InviteBatchDetailResponse?> GetInviteBatchAsync(string ownerMemberId, string batchId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        string? ventureId; string status; DateTimeOffset expiresAt, createdAt;
        await using (var batch = new NpgsqlCommand("""
            SELECT v.venture_id,
                   CASE WHEN b.status='APPROVED' AND b.expires_at<=CURRENT_TIMESTAMP THEN 'EXPIRED' ELSE b.status END,
                   b.expires_at, b.created_at
            FROM social.invite_batch b LEFT JOIN core.venture v ON v.venture_pk=b.venture_pk
            WHERE b.batch_id=@batch_id AND b.owner_member_id=@owner_member_id
            """, connection))
        {
            Add(batch, "batch_id", batchId); Add(batch, "owner_member_id", ownerMemberId);
            await using var reader = await batch.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            ventureId = NullableString(reader, 0); status = reader.GetString(1);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(2); createdAt = reader.GetFieldValue<DateTimeOffset>(3);
        }
        await using var command = new NpgsqlCommand($"""
            SELECT inv.invitation_id, l.link_id, r.contact_record_id, r.label_ciphertext, {EffectiveInvitationStatus("inv")},
                   inv.first_opened_at, inv.accepted_at, inv.effective_expires_at
            FROM (SELECT i.*, LEAST(i.expires_at, b.expires_at) AS effective_expires_at
                  FROM social.invitation i JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
                  WHERE b.batch_id=@batch_id AND b.owner_member_id=@owner_member_id) inv
            JOIN social.contact_link l ON l.contact_link_pk=inv.contact_link_pk
            JOIN social.contact_record r ON r.contact_record_pk=l.contact_record_pk
            ORDER BY inv.batch_position
            """, connection);
        Add(command, "batch_id", batchId); Add(command, "owner_member_id", ownerMemberId);
        var invitations = new List<InvitationStatusResponse>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var recordId = reader.GetString(2);
                invitations.Add(new(reader.GetString(0), reader.GetString(1),
                    identityProtector.UnprotectContactLabel(ownerMemberId, recordId, reader.GetFieldValue<byte[]>(3)), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }
        return new(batchId, ventureId, status, expiresAt, createdAt, invitations);
    }

    // The raw token is never stored, so a resend rotates it. The new token is derived from the
    // resend's Idempotency-Key, so retrying the same resend returns the same token.
    public async Task<InvitationDispatch> ResendInvitationAsync(string ownerMemberId, string invitationId, int expiresInDays, string idempotencyKey, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        long invitationPk, batchPk; string batchId, linkId, status, batchStatus, currentHash; DateTimeOffset currentExpiry;
        await using (var read = new NpgsqlCommand("""
            SELECT i.invitation_pk, b.invite_batch_pk, b.batch_id, l.link_id, i.status, b.status, i.token_hash, LEAST(i.expires_at, b.expires_at)
            FROM social.invitation i
            JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
            JOIN social.contact_link l ON l.contact_link_pk=i.contact_link_pk
            WHERE i.invitation_id=@invitation_id AND i.owner_member_id=@owner_member_id
            FOR UPDATE OF i,b
            """, connection, transaction))
        {
            Add(read, "invitation_id", invitationId); Add(read, "owner_member_id", ownerMemberId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new DomainException("INVITATION_NOT_FOUND", 404);
            invitationPk = reader.GetInt64(0); batchPk = reader.GetInt64(1); batchId = reader.GetString(2); linkId = reader.GetString(3);
            status = reader.GetString(4); batchStatus = reader.GetString(5); currentHash = reader.GetString(6); currentExpiry = reader.GetFieldValue<DateTimeOffset>(7);
        }
        var token = identityProtector.CreateInvitationToken(ownerMemberId, batchId, linkId, $"resend\n{idempotencyKey}");
        var tokenHash = TokenHash(token);
        if (currentHash == tokenHash)
        {
            await transaction.CommitAsync(ct);
            return new(invitationId, linkId, token, currentExpiry);
        }
        if (status is "ACCEPTED" or "REVOKED" || batchStatus is "CANCELLED" or "DRAFT") throw new DomainException("INVITATION_NOT_RESENDABLE", 409);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(expiresInDays);
        await using (var update = new NpgsqlCommand("""
            UPDATE social.invite_batch SET expires_at=GREATEST(expires_at, @expires_at),
                status=CASE WHEN status='EXPIRED' THEN 'APPROVED' ELSE status END, updated_at=CURRENT_TIMESTAMP
            WHERE invite_batch_pk=@batch_pk;
            UPDATE social.invitation SET token_hash=@token_hash, expires_at=@expires_at,
                status=CASE WHEN status='EXPIRED' THEN 'APPROVED' ELSE status END, updated_at=CURRENT_TIMESTAMP
            WHERE invitation_pk=@invitation_pk;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("expires_at", expiresAt); update.Parameters.AddWithValue("batch_pk", batchPk);
            update.Parameters.AddWithValue("invitation_pk", invitationPk); Add(update, "token_hash", tokenHash);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(invitationId, linkId, token, expiresAt);
    }

    public async Task<InvitationPreviewResponse> OpenInvitationAsync(string viewerMemberId, string token, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        InvitationPreviewResponse preview;
        await using (var read = new NpgsqlCommand($"""
            SELECT inv.invitation_id, inv.owner_member_id, COALESCE(p.display_name, ''), v.name, {EffectiveInvitationStatus("inv")}, inv.effective_expires_at
            FROM (SELECT i.*, b.venture_pk, LEAST(i.expires_at, b.expires_at) AS effective_expires_at
                  FROM social.invitation i JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
                  WHERE i.token_hash=@token_hash) inv
            JOIN social.contact_link l ON l.contact_link_pk=inv.contact_link_pk
            LEFT JOIN core.member_profile p ON p.member_id=inv.owner_member_id
            LEFT JOIN core.venture v ON v.venture_pk=inv.venture_pk
            WHERE inv.status<>'REVOKED' AND (l.contact_member_id IS NULL OR l.contact_member_id=@viewer_member_id OR inv.owner_member_id=@viewer_member_id)
            """, connection, transaction))
        {
            Add(read, "token_hash", TokenHash(token)); Add(read, "viewer_member_id", viewerMemberId);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new DomainException("INVITATION_NOT_FOUND", 404);
            preview = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
        }
        // The inviter previewing their own link must not count as the invitee opening it.
        if (preview.Status == "APPROVED" && preview.InviterMemberId != viewerMemberId)
        {
            await using var opened = new NpgsqlCommand("""
                UPDATE social.invitation SET status='OPENED', first_opened_at=COALESCE(first_opened_at, CURRENT_TIMESTAMP), updated_at=CURRENT_TIMESTAMP
                WHERE invitation_id=@invitation_id AND status='APPROVED'
                """, connection, transaction);
            Add(opened, "invitation_id", preview.InvitationId);
            await opened.ExecuteNonQueryAsync(ct);
            preview = preview with { Status = "OPENED" };
        }
        await transaction.CommitAsync(ct);
        return preview;
    }

    public async Task<IReadOnlyList<PendingInvitationResponse>> ListPendingInvitationsAsync(string memberId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT i.invitation_id, i.owner_member_id, COALESCE(p.display_name, ''), p.headline, v.name, i.created_at, LEAST(i.expires_at, b.expires_at)
            FROM social.invitation i
            JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
            JOIN social.contact_link l ON l.contact_link_pk=i.contact_link_pk
            LEFT JOIN core.member_profile p ON p.member_id=i.owner_member_id
            LEFT JOIN core.venture v ON v.venture_pk=b.venture_pk
            WHERE i.status IN ('APPROVED','OPENED') AND i.expires_at>CURRENT_TIMESTAMP
              AND b.status='APPROVED' AND b.expires_at>CURRENT_TIMESTAMP
              AND i.owner_member_id<>@recipient_member_id
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id)
              AND {MatchesRecipientIdentity}
              AND NOT EXISTS (SELECT 1 FROM social.member_block mb WHERE mb.removed_at IS NULL
                  AND ((mb.blocker_member_id=@recipient_member_id AND mb.blocked_member_id=i.owner_member_id)
                    OR (mb.blocker_member_id=i.owner_member_id AND mb.blocked_member_id=@recipient_member_id)))
            ORDER BY i.created_at DESC, i.invitation_pk DESC
            LIMIT 50
            """);
        Add(command, "recipient_member_id", memberId);
        var items = new List<PendingInvitationResponse>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)));
        return items;
    }

    // Revoking again is a no-op, so a retried request succeeds. An accepted invitation has already
    // created a connection and can't be revoked.
    public async Task RevokeInvitationAsync(string ownerMemberId, string invitationId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            WITH target AS (
                SELECT invitation_pk, status FROM social.invitation
                WHERE invitation_id=@invitation_id AND owner_member_id=@owner_member_id
                FOR UPDATE),
            revoked AS (
                UPDATE social.invitation i SET status='REVOKED', updated_at=CURRENT_TIMESTAMP
                FROM target t WHERE i.invitation_pk=t.invitation_pk AND t.status IN ('APPROVED','OPENED','EXPIRED')
                RETURNING i.invitation_pk)
            SELECT t.status FROM target t
            """);
        Add(command, "invitation_id", invitationId); Add(command, "owner_member_id", ownerMemberId);
        var status = (string?)await command.ExecuteScalarAsync(ct);
        if (status is null) throw new DomainException("INVITATION_NOT_FOUND", 404);
        if (status == "ACCEPTED") throw new DomainException("INVITATION_NOT_REVOKABLE", 409);
    }

    // Permanently removes an imported contact: the record's FKs cascade to its identifier hashes, link,
    // invitations, scores and link history. An existing connection/chat with them is kept.
    // Deleting a contact that is already gone (or never was the caller's) is a no-op, so retries succeed.
    public async Task DeleteContactAsync(string ownerMemberId, string linkId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string? origin;
        await using (var read = new NpgsqlCommand("""
            SELECT l.origin_type FROM social.contact_link l
            WHERE l.link_id=@link_id AND l.owner_member_id=@owner_member_id
            FOR UPDATE
            """, connection, transaction))
        {
            Add(read, "link_id", linkId); Add(read, "owner_member_id", ownerMemberId);
            origin = (string?)await read.ExecuteScalarAsync(ct);
        }
        if (origin is null) { await transaction.CommitAsync(ct); return; }
        // Only address-book imports are the owner's own data to delete; met-in-person links are evidence-backed.
        if (origin != "IMPORT") throw new DomainException("CONTACT_NOT_REMOVABLE", 409);
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM social.contact_record r
            USING social.contact_link l
            WHERE l.link_id=@link_id AND l.owner_member_id=@owner_member_id
              AND r.contact_record_pk=l.contact_record_pk AND r.owner_member_id=l.owner_member_id
            """, connection, transaction))
        {
            Add(delete, "link_id", linkId); Add(delete, "owner_member_id", ownerMemberId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task RemoveConnectionAsync(string memberId, string connectionId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string? otherMemberId;
        await using (var disconnect = new NpgsqlCommand("""
            UPDATE social.connection SET status='DISCONNECTED',disconnected_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            WHERE connection_id=@connection_id AND status='ACTIVE' AND @member_id IN (member_low_id,member_high_id)
            RETURNING CASE WHEN member_low_id=@member_id THEN member_high_id ELSE member_low_id END
            """, connection, transaction))
        {
            Add(disconnect, "connection_id", connectionId); Add(disconnect, "member_id", memberId);
            otherMemberId = (string?)await disconnect.ExecuteScalarAsync(ct);
        }
        if (otherMemberId is null)
        {
            await using var existing = new NpgsqlCommand("""
                SELECT CASE WHEN member_low_id=@member_id THEN member_high_id ELSE member_low_id END
                FROM social.connection
                WHERE connection_id=@connection_id AND status='DISCONNECTED' AND @member_id IN (member_low_id,member_high_id)
                """, connection, transaction);
            Add(existing, "connection_id", connectionId); Add(existing, "member_id", memberId);
            otherMemberId = (string?)await existing.ExecuteScalarAsync(ct);
            if (otherMemberId is null) throw new DomainException("CONNECTION_NOT_FOUND", 404);
        }

        await using (var freeze = new NpgsqlCommand("""
            INSERT INTO social.sci_relationship_history (
              relationship_history_id,owner_member_id,subject_reference_id,former_contact_link_id,display_state,freeze_reason,
              last_match_score,last_band,last_layer_code,last_layer_ordinal,last_outcome_points,last_activity_at,frozen_at)
            SELECT 'history_'||substr(md5(link.owner_member_id||':'||link.contact_member_id),1,32),
              link.owner_member_id,link.contact_member_id,link.link_id,'FROZEN','REMOVED',contact.match_score,contact.band,
              relationship.layer_code,relationship.layer_ordinal,COALESCE(relationship.rolling_points,0),relationship.last_activity_at,CURRENT_TIMESTAMP
            FROM social.contact_link link
            LEFT JOIN social.sci_contact_projection contact ON contact.owner_member_id=link.owner_member_id AND contact.contact_link_pk=link.contact_link_pk
            LEFT JOIN social.sci_relationship_projection relationship ON relationship.owner_member_id=link.owner_member_id AND relationship.contact_link_pk=link.contact_link_pk
            WHERE (link.owner_member_id=@member_id AND link.contact_member_id=@other_member_id)
               OR (link.owner_member_id=@other_member_id AND link.contact_member_id=@member_id)
            ON CONFLICT (owner_member_id,subject_reference_id) DO UPDATE SET
              former_contact_link_id=EXCLUDED.former_contact_link_id,display_state='FROZEN',freeze_reason='REMOVED',
              last_match_score=EXCLUDED.last_match_score,last_band=EXCLUDED.last_band,last_layer_code=EXCLUDED.last_layer_code,
              last_layer_ordinal=EXCLUDED.last_layer_ordinal,last_outcome_points=EXCLUDED.last_outcome_points,
              last_activity_at=EXCLUDED.last_activity_at,frozen_at=EXCLUDED.frozen_at;

            UPDATE social.contact_link SET lifecycle_state='REMOVED',history_frozen_at=CURRENT_TIMESTAMP,
              history_freeze_reason='REMOVED',display_state='FROZEN',last_transition_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            WHERE (owner_member_id=@member_id AND contact_member_id=@other_member_id)
               OR (owner_member_id=@other_member_id AND contact_member_id=@member_id);

            UPDATE social.sci_contact_projection projection SET lifecycle_state='REMOVED',counts_in_sci=false,counts_in_potential=false,
              projection_state='FROZEN',frozen_at=CURRENT_TIMESTAMP,freeze_reason='REMOVED',as_of=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            FROM social.contact_link link WHERE link.contact_link_pk=projection.contact_link_pk AND link.owner_member_id=projection.owner_member_id
              AND ((link.owner_member_id=@member_id AND link.contact_member_id=@other_member_id)
                OR (link.owner_member_id=@other_member_id AND link.contact_member_id=@member_id));

            UPDATE social.sci_relationship_projection projection SET projection_status='FROZEN',frozen_at=CURRENT_TIMESTAMP,
              freeze_reason='REMOVED',updated_at=CURRENT_TIMESTAMP
            FROM social.contact_link link WHERE link.contact_link_pk=projection.contact_link_pk AND link.owner_member_id=projection.owner_member_id
              AND ((link.owner_member_id=@member_id AND link.contact_member_id=@other_member_id)
                OR (link.owner_member_id=@other_member_id AND link.contact_member_id=@member_id));

            UPDATE social.sci_summary_projection summary SET sci_count=totals.sci_count,potential_count=totals.potential_count,
              waitlist_count=totals.waitlist_count,repository_count=totals.repository_count,total_contact_count=totals.total_contact_count,
              core_count=totals.core_count,as_of=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP
            FROM (SELECT owner.owner_member_id,
              count(contact.contact_link_pk) FILTER(WHERE contact.counts_in_sci)::int sci_count,
              count(contact.contact_link_pk) FILTER(WHERE contact.band='NETWORK' AND NOT contact.counts_in_sci)::int potential_count,
              count(contact.contact_link_pk) FILTER(WHERE contact.band='WAITLIST')::int waitlist_count,
              count(contact.contact_link_pk) FILTER(WHERE contact.band='REPOSITORY')::int repository_count,
              count(contact.contact_link_pk)::int total_contact_count,
              count(contact.contact_link_pk) FILTER(WHERE contact.layer_ordinal=1)::int core_count
              FROM (VALUES(@member_id),(@other_member_id)) owner(owner_member_id)
              LEFT JOIN social.sci_contact_projection contact ON contact.owner_member_id=owner.owner_member_id AND contact.projection_state='ACTIVE'
              GROUP BY owner.owner_member_id) totals WHERE summary.owner_member_id=totals.owner_member_id;
            """, connection, transaction))
        {
            Add(freeze, "member_id", memberId); Add(freeze, "other_member_id", otherMemberId);
            await freeze.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<MemberDiscoveryPreferenceResponse> GetDiscoveryPreferenceAsync(string memberId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT find_by_phone,find_by_email,row_version,updated_at
            FROM core.member_discovery_preference WHERE member_id=@member_id
            """);
        Add(command,"member_id",memberId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetBoolean(0),reader.GetBoolean(1),$"\"{reader.GetInt64(2):x}\"",reader.GetFieldValue<DateTimeOffset>(3))
            : new(false,false,"\"0\"",DateTimeOffset.UnixEpoch);
    }

    public async Task<MemberDiscoveryPreferenceResponse> SaveDiscoveryPreferenceAsync(string memberId, MemberDiscoveryPreferenceRequest request, string expectedETag, CancellationToken ct)
    {
        var token=expectedETag.Trim().Trim('"');
        if (!long.TryParse(token,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var expectedVersion) || expectedVersion<0)
            throw new DomainException("ETAG_MISMATCH",412);
        var sql=expectedVersion==0 ? """
            INSERT INTO core.member_discovery_preference(discovery_preference_id,member_id,find_by_phone,find_by_email)
            VALUES('discovery_'||substr(md5(@member_id),1,32),@member_id,@find_by_phone,@find_by_email)
            ON CONFLICT(member_id) DO NOTHING
            RETURNING find_by_phone,find_by_email,row_version,updated_at
            """ : """
            UPDATE core.member_discovery_preference SET
              find_by_phone=@find_by_phone,find_by_email=@find_by_email,updated_at=CURRENT_TIMESTAMP
            WHERE member_id=@member_id AND row_version=@expected_version
            RETURNING find_by_phone,find_by_email,row_version,updated_at
            """;
        await using var command=dataSource.CreateCommand(sql);
        Add(command,"member_id",memberId);command.Parameters.AddWithValue("find_by_phone",request.FindByPhone);
        command.Parameters.AddWithValue("find_by_email",request.FindByEmail);command.Parameters.AddWithValue("expected_version",expectedVersion);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new DomainException("ETAG_MISMATCH",412);
        return new(reader.GetBoolean(0),reader.GetBoolean(1),$"\"{reader.GetInt64(2):x}\"",reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<InAppNotificationPage> GetInAppNotificationsAsync(string memberId,string? cursor,int limit,CancellationToken ct)
    {
        var (afterCreatedAt,afterId)=DecodeNotificationCursor(cursor);
        await using var command=dataSource.CreateCommand("""
            SELECT notification_id,purpose_code,resource_type,resource_id,template_code,status,
                   scheduled_at,expires_at,created_at,row_version
            FROM notification.notification
            WHERE member_id=@member_id AND channel='IN_APP' AND status<>'SUPPRESSED'
              AND expires_at>CURRENT_TIMESTAMP
              AND (@after_created_at IS NULL OR (created_at,notification_id)<(@after_created_at,@after_id))
            ORDER BY created_at DESC,notification_id DESC LIMIT @take
            """);
        Add(command,"member_id",memberId);
        command.Parameters.Add("after_created_at",NpgsqlDbType.TimestampTz).Value=afterCreatedAt is null?DBNull.Value:afterCreatedAt.Value;
        command.Parameters.Add("after_id",NpgsqlDbType.Varchar).Value=afterId is null?DBNull.Value:afterId;
        command.Parameters.AddWithValue("take",limit+1);
        var rows=new List<InAppNotificationResponse>();
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0),reader.GetString(1),NullableString(reader,2),NullableString(reader,3),
            reader.GetString(4),reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6),reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),$"\"{reader.GetInt64(9):x}\""));
        var page=rows.Take(limit).ToArray();
        return new(page,rows.Count>limit?EncodeNotificationCursor(page[^1].CreatedAt,page[^1].NotificationId):null);
    }

    // The imported contact's phone/email hash equals the recipient's verified identity hash
    // (both come from IIdentityProtector's keyed lookup hash). Expects alias l (contact_link) and @recipient_member_id.
    private const string MatchesRecipientIdentity = """
        EXISTS (SELECT 1 FROM social.contact_identifier ci
                JOIN iam.member_identity mi ON mi.provider=ci.identifier_type AND mi.provider_subject_hash=ci.identifier_hash
                WHERE ci.contact_record_pk=l.contact_record_pk AND mi.member_id=@recipient_member_id
                  AND mi.status='ACTIVE' AND mi.verified_at IS NOT NULL)
        """;

    // Unaccepted invitations past their own or their batch's expiry read as EXPIRED. Expects alias.status and alias.effective_expires_at.
    private static string EffectiveInvitationStatus(string alias) =>
        $"CASE WHEN {alias}.status IN ('APPROVED','OPENED') AND {alias}.effective_expires_at<=CURRENT_TIMESTAMP THEN 'EXPIRED' ELSE {alias}.status END";

    private static async Task<VentureResponse?> ReadVentureAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string ownerMemberId, string ventureId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT v.venture_id, v.name, v.organization_id, v.industry_code, v.stage_code,
                   v.revenue_band_code, v.team_size_band_code, v.status, v.updated_at, v.row_version,
                   COALESCE(array_agg(g.geography_code ORDER BY g.is_primary DESC, g.geography_code)
                       FILTER (WHERE g.geography_code IS NOT NULL), ARRAY[]::varchar[])
            FROM core.venture v LEFT JOIN core.venture_geography g ON g.venture_pk=v.venture_pk
            WHERE v.venture_id=@venture_id AND v.owner_member_id=@owner_member_id
            GROUP BY v.venture_pk
            """, connection, transaction);
        Add(command, "venture_id", ventureId); Add(command, "owner_member_id", ownerMemberId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVenture(reader) : null;
    }

    private static VentureResponse ReadVenture(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetFieldValue<string[]>(10),
        reader.GetFieldValue<DateTimeOffset>(8),
        $"\"{reader.GetInt64(9):x}\"");

    private static async Task<RelationshipOutcomeResponse?> ReadOutcomeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string outcomeId, string memberId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT outcome.outcome_id, outcome.outcome_type, outcome.status, outcome.initiated_by_member_id,
                   other_party.member_id, outcome.occurred_at, outcome.confirmation_due_at,
                   outcome.verified_at, outcome.withdrawal_due_at, outcome.valid_until,
                   (CASE WHEN outcome.status='VERIFIED' THEN
                       CASE outcome.outcome_type WHEN 'MEETING' THEN 1 WHEN 'INTRODUCTION' THEN 2 ELSE 3 END
                    ELSE 0 END)::numeric,
                   (SELECT count(DISTINCT confirmation.actor_member_id)::int
                    FROM social.relationship_outcome_confirmation confirmation
                    WHERE confirmation.relationship_outcome_pk=outcome.relationship_outcome_pk
                      AND confirmation.actor_type='MEMBER' AND confirmation.decision='CONFIRM'),
                   outcome.row_version
            FROM social.relationship_outcome outcome
            JOIN social.relationship_outcome_party own_party
              ON own_party.relationship_outcome_pk=outcome.relationship_outcome_pk AND own_party.member_id=@member_id
            JOIN social.relationship_outcome_party other_party
              ON other_party.relationship_outcome_pk=outcome.relationship_outcome_pk AND other_party.member_id<>@member_id
             AND other_party.party_role IN ('INITIATOR','PARTICIPANT')
            WHERE outcome.outcome_id=@outcome_id
            LIMIT 1
            """, connection, transaction);
        Add(command, "outcome_id", outcomeId); Add(command, "member_id", memberId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7)?null:reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8)?null:reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9)?null:reader.GetFieldValue<DateTimeOffset>(9), reader.GetDecimal(10), reader.GetInt32(11), $"\"{reader.GetInt64(12):x}\"");
    }

    private static async Task ReprojectRelationshipsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string outcomeId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH affected_members AS (
                SELECT party.member_id
                FROM social.relationship_outcome outcome
                JOIN social.relationship_outcome_party party ON party.relationship_outcome_pk=outcome.relationship_outcome_pk
                WHERE outcome.outcome_id=@outcome_id AND party.party_role IN ('INITIATOR','PARTICIPANT')
            ), affected_links AS (
                SELECT link.owner_member_id, link.contact_link_pk, link.contact_member_id
                FROM social.contact_link link
                WHERE link.owner_member_id IN (SELECT member_id FROM affected_members)
                  AND link.contact_member_id IN (SELECT member_id FROM affected_members)
                  AND link.owner_member_id<>link.contact_member_id
            ), statistics AS (
                SELECT link.owner_member_id, link.contact_link_pk,
                       COALESCE(sum(CASE outcome.outcome_type WHEN 'MEETING' THEN 1 WHEN 'INTRODUCTION' THEN 2 ELSE 3 END)
                           FILTER (WHERE outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP),0)::numeric(8,2) AS points,
                       count(*) FILTER (WHERE outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP)::int AS verified_count,
                       bool_or(outcome.outcome_type='INTRODUCTION' AND outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP) AS has_introduction,
                       bool_or(outcome.outcome_type='RESULT' AND outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP) AS has_result,
                       max(outcome.verified_at) FILTER (WHERE outcome.status='VERIFIED' AND outcome.valid_until>CURRENT_TIMESTAMP) AS last_activity_at
                FROM affected_links link
                LEFT JOIN social.relationship_outcome outcome ON
                    EXISTS (SELECT 1 FROM social.relationship_outcome_party p1
                            WHERE p1.relationship_outcome_pk=outcome.relationship_outcome_pk AND p1.member_id=link.owner_member_id)
                    AND EXISTS (SELECT 1 FROM social.relationship_outcome_party p2
                                WHERE p2.relationship_outcome_pk=outcome.relationship_outcome_pk AND p2.member_id=link.contact_member_id)
                GROUP BY link.owner_member_id, link.contact_link_pk
            )
            INSERT INTO social.sci_relationship_projection (
                relationship_projection_id, owner_member_id, contact_link_pk, rolling_points,
                verified_outcome_count, has_introduction, has_result, tie_strength,
                layer_code, layer_ordinal, last_activity_at, quiet_since, calculated_at)
            SELECT 'rel_' || substr(md5(owner_member_id || ':' || contact_link_pk::text),1,32),
                   owner_member_id, contact_link_pk, points, verified_count,
                   COALESCE(has_introduction,false), COALESCE(has_result,false), LEAST(100,points*10),
                   CASE WHEN verified_count>0 THEN 'QUALIFIED_NETWORK' END,
                   CASE WHEN verified_count>0 THEN 5::smallint END,
                   last_activity_at,
                   CASE WHEN last_activity_at IS NOT NULL AND last_activity_at<CURRENT_TIMESTAMP-interval '180 days'
                        THEN last_activity_at + interval '180 days' END,
                   CURRENT_TIMESTAMP
            FROM statistics
            ON CONFLICT (owner_member_id, contact_link_pk) DO UPDATE SET
                rolling_points=EXCLUDED.rolling_points, verified_outcome_count=EXCLUDED.verified_outcome_count,
                has_introduction=EXCLUDED.has_introduction, has_result=EXCLUDED.has_result,
                tie_strength=EXCLUDED.tie_strength, layer_code=EXCLUDED.layer_code,
                layer_ordinal=EXCLUDED.layer_ordinal, last_activity_at=EXCLUDED.last_activity_at,
                quiet_since=EXCLUDED.quiet_since, calculated_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP;

            WITH affected_members AS (
                SELECT party.member_id
                FROM social.relationship_outcome outcome
                JOIN social.relationship_outcome_party party ON party.relationship_outcome_pk=outcome.relationship_outcome_pk
                WHERE outcome.outcome_id=@outcome_id AND party.party_role IN ('INITIATOR','PARTICIPANT')
            ), ranked AS (
                SELECT projection.sci_relationship_projection_pk,
                       CASE WHEN row_number() OVER (PARTITION BY projection.owner_member_id
                                  ORDER BY projection.rolling_points DESC, projection.last_activity_at DESC,
                                           projection.contact_link_pk) <= 5 THEN 1
                            WHEN row_number() OVER (PARTITION BY projection.owner_member_id
                                  ORDER BY projection.rolling_points DESC, projection.last_activity_at DESC,
                                           projection.contact_link_pk) <= 15 THEN 2
                            WHEN row_number() OVER (PARTITION BY projection.owner_member_id
                                  ORDER BY projection.rolling_points DESC, projection.last_activity_at DESC,
                                           projection.contact_link_pk) <= 50 THEN 3
                            WHEN row_number() OVER (PARTITION BY projection.owner_member_id
                                  ORDER BY projection.rolling_points DESC, projection.last_activity_at DESC,
                                           projection.contact_link_pk) <= 150 THEN 4 ELSE 5 END AS base_ordinal,
                       projection.last_activity_at
                FROM social.sci_relationship_projection projection
                WHERE projection.owner_member_id IN (SELECT member_id FROM affected_members)
                  AND projection.projection_status='ACTIVE' AND projection.verified_outcome_count>0
            ), final_rank AS (
                SELECT sci_relationship_projection_pk,
                       LEAST(5, base_ordinal + CASE WHEN last_activity_at<CURRENT_TIMESTAMP-interval '180 days' THEN 1 ELSE 0 END)::smallint AS ordinal
                FROM ranked
            )
            UPDATE social.sci_relationship_projection projection
            SET layer_ordinal=rank.ordinal,
                layer_code=CASE rank.ordinal WHEN 1 THEN 'STRATEGIC_PARTNERS' WHEN 2 THEN 'DEAL_PARTNERS'
                    WHEN 3 THEN 'VERIFIED_COLLABORATORS' WHEN 4 THEN 'ACTIVE_NETWORK' ELSE 'QUALIFIED_NETWORK' END,
                updated_at=CURRENT_TIMESTAMP
            FROM final_rank rank
            WHERE projection.sci_relationship_projection_pk=rank.sci_relationship_projection_pk;

            UPDATE social.sci_contact_projection contact
            SET layer_code=CASE WHEN contact.counts_in_sci THEN relationship.layer_code END,
                layer_ordinal=CASE WHEN contact.counts_in_sci THEN relationship.layer_ordinal END,
                outcome_points=relationship.rolling_points, tie_strength=relationship.tie_strength,
                last_activity_at=relationship.last_activity_at, as_of=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP
            FROM social.sci_relationship_projection relationship
            WHERE relationship.owner_member_id=contact.owner_member_id
              AND relationship.contact_link_pk=contact.contact_link_pk
              AND relationship.owner_member_id IN (
                  SELECT party.member_id FROM social.relationship_outcome outcome
                  JOIN social.relationship_outcome_party party ON party.relationship_outcome_pk=outcome.relationship_outcome_pk
                  WHERE outcome.outcome_id=@outcome_id)
            """, connection, transaction);
        Add(command, "outcome_id", outcomeId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertIdentifierAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long recordPk, string ownerMemberId, string type, string? hash, CancellationToken ct)
    {
        if (hash is null) return;
        await using var command = new NpgsqlCommand("""
            INSERT INTO social.contact_identifier (contact_record_pk, owner_member_id, identifier_type, identifier_hash, hash_key_version, source_type)
            VALUES (@contact_record_pk, @owner_member_id, @identifier_type, @identifier_hash, 'identity-v1', 'DEVICE_IMPORT')
            ON CONFLICT (owner_member_id, identifier_type, identifier_hash, hash_key_version) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("contact_record_pk", recordPk); Add(command, "owner_member_id", ownerMemberId); Add(command, "identifier_type", type); Add(command, "identifier_hash", hash);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddNullableBytea(NpgsqlCommand command, string name, byte[]? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bytea).Value = value is null ? DBNull.Value : value;

    private static void Add(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value is null ? DBNull.Value : value;
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static string EncodePkCursor(long pk) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pk.ToString(CultureInfo.InvariantCulture)));
    private static string EncodeNotificationCursor(DateTimeOffset createdAt,string notificationId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt.UtcTicks}\n{notificationId}"));
    private static (DateTimeOffset? CreatedAt,string? NotificationId) DecodeNotificationCursor(string? cursor)
    {
        if(string.IsNullOrWhiteSpace(cursor)) return (null,null);
        try
        {
            var parts=Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if(parts.Length!=2||!long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out var ticks)||ticks<=0
               ||string.IsNullOrWhiteSpace(parts[1])||parts[1].Length>64) throw new FormatException();
            return (new DateTimeOffset(ticks,TimeSpan.Zero),parts[1]);
        }
        catch(FormatException){throw new DomainException("NOTIFICATION_CURSOR_INVALID");}
    }
    private static long DecodePkCursor(string? cursor, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            return long.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None, CultureInfo.InvariantCulture, out var pk) && pk > 0
                ? pk : throw new FormatException();
        }
        catch (FormatException) { throw new DomainException(errorCode); }
    }
    private static string EncodeSciCursor(int score, string projectionId) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{score.ToString(CultureInfo.InvariantCulture)}\n{projectionId}"));
    private static string StableId(string prefix, string value) => $"{prefix}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32]}";
    private static (int? Score, string? ProjectionId) DecodeSciCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (null, null);
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
                || string.IsNullOrWhiteSpace(parts[1]) || parts[1].Length > 64) throw new FormatException();
            return (score, parts[1]);
        }
        catch (FormatException) { throw new DomainException("SCI_CURSOR_INVALID"); }
    }
}
