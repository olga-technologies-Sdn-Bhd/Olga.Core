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

    public async Task<ContactImportResponse> ImportContactsAsync(string ownerMemberId, IReadOnlyList<ProtectedContactImport> contacts, int retentionDays, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        foreach (var item in contacts)
        {
            long recordPk;
            await using (var command = new NpgsqlCommand("""
                INSERT INTO social.contact_record (contact_record_id, owner_member_id, label_ciphertext, source_type, expires_at)
                VALUES (@contact_record_id, @owner_member_id, @label_ciphertext, 'DEVICE_IMPORT', CURRENT_TIMESTAMP + make_interval(days => @retention_days))
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

    public async Task<InvitationAcceptResponse> AcceptInvitationAsync(string recipientMemberId, string token, string idempotencyKey, CancellationToken ct)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string invitationId, senderMemberId, linkId;
        long? venturePk;
        await using (var read = new NpgsqlCommand("""
            SELECT i.invitation_id, i.owner_member_id, l.link_id, b.venture_pk
            FROM social.invitation i
            JOIN social.invite_batch b ON b.invite_batch_pk=i.invite_batch_pk
            JOIN social.contact_link l ON l.contact_link_pk=i.contact_link_pk
            WHERE i.token_hash=@token_hash AND i.status IN ('APPROVED','OPENED','ACCEPTED')
              AND i.expires_at>CURRENT_TIMESTAMP AND b.expires_at>CURRENT_TIMESTAMP
              AND (l.contact_member_id IS NULL OR l.contact_member_id=@recipient_member_id)
            FOR UPDATE OF i,l
            """, connection, transaction))
        {
            Add(read, "token_hash", tokenHash); Add(read, "recipient_member_id", recipientMemberId);
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

    private static async Task<VentureResponse?> ReadVentureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string ownerMemberId, string ventureId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT v.name, v.status, v.updated_at, v.row_version,
                   COALESCE(array_agg(g.geography_code ORDER BY g.is_primary DESC, g.geography_code) FILTER (WHERE g.geography_code IS NOT NULL), ARRAY[]::varchar[])
            FROM core.venture v LEFT JOIN core.venture_geography g ON g.venture_pk=v.venture_pk
            WHERE v.venture_id=@venture_id AND v.owner_member_id=@owner_member_id
            GROUP BY v.venture_pk
            """, connection, transaction);
        Add(command, "venture_id", ventureId); Add(command, "owner_member_id", ownerMemberId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new VentureResponse(ventureId, reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(4), reader.GetFieldValue<DateTimeOffset>(2), $"\"{reader.GetInt64(3):x}\"") : null;
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

    private static void Add(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value is null ? DBNull.Value : value;
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
