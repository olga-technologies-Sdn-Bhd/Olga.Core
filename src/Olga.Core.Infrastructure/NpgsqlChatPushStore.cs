using Npgsql;
using NpgsqlTypes;
using Olga.Core.Application;

namespace Olga.Core.Infrastructure;

public sealed class NpgsqlChatPushStore(NpgsqlDataSource dataSource) : IChatPushStore
{
    private const int CommandTimeoutSeconds = 15;

    // One row about the message and its recipient: mute, CHAT preference, blocks, whether the chat is still open,
    // the sender's name and the attached file's type.
    public const string MessageFactsSql = """
        SELECT m.message_type,
               m.deleted_at IS NOT NULL AS deleted,
               (SELECT f.media_type
                FROM storage.file_asset_link l
                JOIN storage.file_asset f ON f.file_asset_id = l.file_asset_id
                WHERE l.resource_type = 'MESSAGE' AND l.resource_id = m.message_id AND l.removed_at IS NULL
                LIMIT 1) AS media_type,
               p.muted_until,
               (SELECT np.push_enabled FROM notification.notification_preference np
                WHERE np.member_id = p.member_id AND np.purpose_code = @purpose) AS chat_push_enabled,
               EXISTS (SELECT 1 FROM social.member_block b
                       WHERE b.removed_at IS NULL
                         AND ((b.blocker_member_id = m.sender_member_id AND b.blocked_member_id = p.member_id)
                           OR (b.blocker_member_id = p.member_id AND b.blocked_member_id = m.sender_member_id))) AS blocked,
               (c.status = 'ACTIVE' AND cn.status = 'ACTIVE' AND p.left_at IS NULL) AS conversation_open,
               sp.display_name
        FROM chat.message m
        JOIN chat.conversation c ON c.conversation_id = m.conversation_id
        JOIN social.connection cn ON cn.connection_id = c.connection_id
        JOIN chat.conversation_participant p ON p.conversation_id = m.conversation_id AND p.member_id = @recipient_member_id
        LEFT JOIN core.member_profile sp ON sp.member_id = m.sender_member_id
        WHERE m.message_id = @message_id;
        """;

    public const string TargetsSql = """
        SELECT t.push_token_id, t.device_id, t.provider, t.token_ciphertext
        FROM notification.push_token t
        JOIN iam.member_device d ON d.device_id = t.device_id
        WHERE d.member_id = @recipient_member_id AND d.status = 'ACTIVE' AND t.status = 'ACTIVE'
        ORDER BY t.push_token_id;
        """;

    public async Task<ChatPushFacts?> GetMessageFactsAsync(string messageId, string recipientMemberId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        string messageType; bool deleted, blocked, open; string? mediaType, senderName; DateTimeOffset? mutedUntil; bool? chatPushEnabled;
        await using (var command = new NpgsqlCommand(MessageFactsSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            command.Parameters.Add(new("message_id", NpgsqlDbType.Varchar) { Value = messageId });
            command.Parameters.Add(new("recipient_member_id", NpgsqlDbType.Varchar) { Value = recipientMemberId });
            command.Parameters.Add(new("purpose", NpgsqlDbType.Varchar) { Value = ChatPush.Purpose });
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            messageType = reader.GetString(0);
            deleted = reader.GetBoolean(1);
            mediaType = reader.IsDBNull(2) ? null : reader.GetString(2);
            mutedUntil = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
            chatPushEnabled = reader.IsDBNull(4) ? null : reader.GetBoolean(4);
            blocked = reader.GetBoolean(5);
            open = !reader.IsDBNull(6) && reader.GetBoolean(6);
            senderName = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        var targets = new List<PushTarget>();
        await using (var command = new NpgsqlCommand(TargetsSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            command.Parameters.Add(new("recipient_member_id", NpgsqlDbType.Varchar) { Value = recipientMemberId });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                targets.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3)));
        }
        return new(messageType, deleted, mediaType, mutedUntil, chatPushEnabled, blocked, open, senderName, targets);
    }

    public async Task RecordResultAsync(long pushTokenId, bool delivered, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(delivered
            ? "UPDATE notification.push_token SET last_success_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP WHERE push_token_id = @push_token_id"
            : "UPDATE notification.push_token SET status = 'INVALID', invalidated_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP WHERE push_token_id = @push_token_id AND status = 'ACTIVE'",
            connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add(new("push_token_id", NpgsqlDbType.Bigint) { Value = pushTokenId });
        await command.ExecuteNonQueryAsync(ct);
    }
}
