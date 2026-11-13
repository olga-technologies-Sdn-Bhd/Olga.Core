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
        foreach (var item in contacts)
        {
            long recordPk;
            await using (var command = new NpgsqlCommand("""
                INSERT INTO social.contact_record (contact_record_id, owner_member_id, label_ciphertext, source_type, expires_at)
                VALUES (@contact_record_id, @owner_member_id, @label_ciphertext, 'PHONE', CURRENT_TIMESTAMP + make_interval(days => @retention_days))
                ON CONFLICT (contact_record_id) DO UPDATE SET
                    label_ciphertext = EXCLUDED.label_ciphertext,
                    last_refreshed_at = CURRENT_TIMESTAMP,
                    expires_at = EXCLUDED.expires_at
                WHERE social.contact_record.owner_member_id = EXCLUDED.owner_member_id
                RETURNING contact_record_pk
                """, connection, transaction))
            {
                Add(command, "contact_record_id", item.ContactRecordId); Add(command, "owner_member_id", ownerMemberId);
                command.Parameters.Add("label_ciphertext", NpgsqlDbType.Bytea).Value = item.LabelCiphertext;
                command.Parameters.AddWithValue("retention_days", retentionDays);
                recordPk = Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? throw new DomainException("CONTACT_ID_CONFLICT", 409), CultureInfo.InvariantCulture);
            }
            await UpsertIdentifierAsync(connection, transaction, recordPk, ownerMemberId, "EMAIL", item.EmailHash, ct);
            await UpsertIdentifierAsync(connection, transaction, recordPk, ownerMemberId, "PHONE", item.PhoneHash, ct);
            await using var link = new NpgsqlCommand("""
                INSERT INTO social.contact_link (link_id, owner_member_id, contact_record_pk, origin_type, lifecycle_state)
                VALUES (@link_id, @owner_member_id, @contact_record_pk, 'IMPORT', 'IMPORTED')
                ON CONFLICT (link_id) DO NOTHING
                """, connection, transaction);
            Add(link, "link_id", item.LinkId); Add(link, "owner_member_id", ownerMemberId); link.Parameters.AddWithValue("contact_record_pk", recordPk);
            if (await link.ExecuteNonQueryAsync(ct) == 0)
            {
                await using var ownerCheck = new NpgsqlCommand("SELECT count(*) FROM social.contact_link WHERE link_id=@link_id AND owner_member_id=@owner_member_id AND contact_record_pk=@contact_record_pk", connection, transaction);
                Add(ownerCheck, "link_id", item.LinkId); Add(ownerCheck, "owner_member_id", ownerMemberId); ownerCheck.Parameters.AddWithValue("contact_record_pk", recordPk);
                if (Convert.ToInt32(await ownerCheck.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 1) throw new DomainException("CONTACT_LINK_ID_CONFLICT", 409);
            }
        }
        await transaction.CommitAsync(ct);
        return new(contacts.Count, contacts.Select(x => x.LinkId).ToArray());
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
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string invitationId, senderMemberId, linkId;
        long? venturePk;
        await using (var read = new NpgsqlCommand($"""
            SELECT i.invitation_id, i.owner_member_id, l.link_id, b.venture_pk
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
            invitationId=reader.GetString(0); senderMemberId=reader.GetString(1); linkId=reader.GetString(2); venturePk=reader.IsDBNull(3)?null:reader.GetInt64(3);
        }
        if (senderMemberId == recipientMemberId) throw new DomainException("INVITATION_SELF_ACCEPT_INVALID", 409);
        var requestId = StableId("ireq", $"{invitationId}:{recipientMemberId}");
        var connectionId = StableId("conn", $"{invitationId}:{recipientMemberId}");
        var conversationId = StableId("chat", $"{invitationId}:{recipientMemberId}");
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{invitationId}\n{recipientMemberId}"))).ToLowerInvariant();
        await using (var request = new NpgsqlCommand("""
            INSERT INTO social.connection_request (connection_request_id,sender_member_id,recipient_member_id,context_id,status,expires_at)
            VALUES (@request_id,@sender_member_id,@recipient_member_id,'SCI_INVITATION','PENDING',CURRENT_TIMESTAMP + interval '1 day')
            ON CONFLICT (connection_request_id) DO NOTHING
            """, connection, transaction))
        { Add(request,"request_id",requestId); Add(request,"sender_member_id",senderMemberId); Add(request,"recipient_member_id",recipientMemberId); await request.ExecuteNonQueryAsync(ct); }
        await using (var accept = new NpgsqlCommand("SELECT * FROM social.accept_connection_request(@request_id,@recipient_member_id,@connection_id,@conversation_id,@idempotency_key,@request_hash,CURRENT_TIMESTAMP + interval '1 day',CURRENT_TIMESTAMP + interval '30 days')", connection, transaction))
        {
            Add(accept,"request_id",requestId); Add(accept,"recipient_member_id",recipientMemberId); Add(accept,"connection_id",connectionId); Add(accept,"conversation_id",conversationId);
            Add(accept,"idempotency_key",idempotencyKey); Add(accept,"request_hash",requestHash); await accept.ExecuteNonQueryAsync(ct);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE social.invitation i SET status='ACCEPTED', accepted_at=COALESCE(accepted_at,CURRENT_TIMESTAMP), updated_at=CURRENT_TIMESTAMP
            FROM social.contact_link l
            WHERE i.invitation_id=@invitation_id AND l.contact_link_pk=i.contact_link_pk
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id);
            UPDATE social.contact_link l SET contact_member_id=@recipient_member_id, connection_id=@connection_id,
              lifecycle_state='CONNECTED', consent_at=COALESCE(consent_at,CURRENT_TIMESTAMP), joined_at=COALESCE(joined_at,CURRENT_TIMESTAMP),
              last_transition_at=CURRENT_TIMESTAMP, updated_at=CURRENT_TIMESTAMP
            FROM social.invitation i WHERE i.invitation_id=@invitation_id AND i.contact_link_pk=l.contact_link_pk
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id);
            """, connection, transaction))
        { Add(update,"invitation_id",invitationId); Add(update,"recipient_member_id",recipientMemberId); Add(update,"connection_id",connectionId); await update.ExecuteNonQueryAsync(ct); }
        if (venturePk is not null)
        {
            await using var enqueue = new NpgsqlCommand("""
                SELECT nlp.enqueue_sci_scoring_job(@job_id, v.owner_member_id, v.venture_id, @recipient_member_id, 'INVITATION_ACCEPTED', @dedupe_key, CURRENT_TIMESTAMP)
                FROM core.venture v WHERE v.venture_pk=@venture_pk
                """, connection, transaction);
            Add(enqueue,"job_id",StableId("sci",invitationId)); Add(enqueue,"recipient_member_id",recipientMemberId); Add(enqueue,"dedupe_key",$"invitation-accepted:{invitationId}"); enqueue.Parameters.AddWithValue("venture_pk",venturePk.Value);
            await enqueue.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(invitationId,connectionId,conversationId,linkId);
    }

    public async Task<SciContactPage> GetSciAsync(string ownerMemberId, string ventureId, string? cursor, int limit, CancellationToken ct)
    {
        var (afterScore, afterPk) = DecodeCursor(cursor);
        await using var command = dataSource.CreateCommand("""
            SELECT contact_link_pk, link_id, contact_member_id, contact_record_id, label_ciphertext,
                   calibrated_score, relationship_role, competitor_overlay, is_stale, origin_type, scored_at, score_row_version
            FROM social.vw_sci_contact
            WHERE owner_member_id=@owner_member_id AND venture_id=@venture_id
              AND (@after_score IS NULL OR (calibrated_score, contact_link_pk) < (@after_score, @after_pk))
            ORDER BY calibrated_score DESC, contact_link_pk DESC
            LIMIT @take
            """);
        Add(command, "owner_member_id", ownerMemberId); Add(command, "venture_id", ventureId);
        command.Parameters.Add("after_score", NpgsqlDbType.Numeric).Value = afterScore is null ? DBNull.Value : afterScore.Value;
        command.Parameters.AddWithValue("after_pk", afterPk); command.Parameters.AddWithValue("take", limit + 1);
        var rows = new List<(long Pk, decimal Score, SciContactResponse Value)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var recordId = reader.GetString(3); var ciphertext = reader.GetFieldValue<byte[]>(4); var score = reader.GetDecimal(5); var pk = reader.GetInt64(0);
            rows.Add((pk, score, new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                identityProtector.UnprotectContactLabel(ownerMemberId, recordId, ciphertext), score, reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8),
                reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10), $"\"{reader.GetInt64(11):x}\"")));
        }
        var page = rows.Take(limit).ToArray();
        return new(page.Select(x => x.Value).ToArray(), rows.Count > limit ? EncodeCursor(page[^1].Score, page[^1].Pk) : null);
    }

    public async Task<SciSummaryResponse> GetSciSummaryAsync(string ownerMemberId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var ventures = new List<SciVentureSummary>();
        await using (var perVenture = new NpgsqlCommand("""
            SELECT v.venture_id, v.name, count(s.contact_link_pk)::int, max(s.calibrated_score), round(avg(s.calibrated_score), 2),
                   (count(*) FILTER (WHERE s.is_stale))::int, max(s.scored_at)
            FROM core.venture v
            LEFT JOIN social.vw_sci_contact s ON s.owner_member_id=v.owner_member_id AND s.venture_id=v.venture_id
            WHERE v.owner_member_id=@owner_member_id
            GROUP BY v.venture_pk
            ORDER BY v.updated_at DESC, v.venture_pk DESC
            """, connection))
        {
            Add(perVenture, "owner_member_id", ownerMemberId);
            await using var reader = await perVenture.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ventures.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), NullableDecimal(reader, 3), NullableDecimal(reader, 4),
                    reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }
        // One contact can sit in several ventures' SCI; the totals count each contact once, at its best score.
        await using var totals = new NpgsqlCommand("""
            WITH best AS (
                SELECT contact_link_pk, max(calibrated_score) AS score, max(scored_at) AS scored_at
                FROM social.vw_sci_contact WHERE owner_member_id=@owner_member_id
                GROUP BY contact_link_pk)
            SELECT count(*)::int,
                   (count(*) FILTER (WHERE score >= 10 AND score < 25))::int,
                   (count(*) FILTER (WHERE score >= 25 AND score < 50))::int,
                   (count(*) FILTER (WHERE score >= 50 AND score < 75))::int,
                   (count(*) FILTER (WHERE score >= 75))::int,
                   round(avg(score), 2), max(scored_at)
            FROM best
            """, connection);
        Add(totals, "owner_member_id", ownerMemberId);
        await using var total = await totals.ExecuteReaderAsync(ct);
        await total.ReadAsync(ct);
        return new(ventures.Count, total.GetInt32(0), new(total.GetInt32(1), total.GetInt32(2), total.GetInt32(3), total.GetInt32(4)),
            NullableDecimal(total, 5), total.IsDBNull(6) ? null : total.GetFieldValue<DateTimeOffset>(6), ventures);
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

    private static void Add(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value is null ? DBNull.Value : value;
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static string EncodePkCursor(long pk) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pk.ToString(CultureInfo.InvariantCulture)));
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
    private static string EncodeCursor(decimal score, long pk) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{score.ToString(CultureInfo.InvariantCulture)}:{pk}"));
    private static string StableId(string prefix, string value) => $"{prefix}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32]}";
    private static (decimal? Score, long Pk) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (null, 0);
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length != 2 || !decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var score) || !long.TryParse(parts[1], out var pk)) throw new FormatException();
            return (score, pk);
        }
        catch (FormatException) { throw new DomainException("SCI_CURSOR_INVALID"); }
    }
}
