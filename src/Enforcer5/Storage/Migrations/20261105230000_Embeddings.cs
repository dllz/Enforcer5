using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <summary>
    /// Text and image vectors (build step 15): <c>message_embeddings</c> and <c>media_vectors</c>, of pgvector's
    /// <c>halfvec</c> without a fixed length (a new model needs no migration; its vectors carry its name). Written by hand and
    /// kept out of the EF model, because the type is the extension's: the tables are only created where the <c>vector</c>
    /// extension is enabled (a superuser's <c>CREATE EXTENSION vector</c>, done in both databases on 2026-11-05), qualified
    /// with its schema, which need not be on the search path. Elsewhere this does nothing and the AI signs stay off
    /// (<c>IEmbeddingRepository.AvailableAsync</c>).
    /// </summary>
    public partial class Embeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE s text;
BEGIN
    SELECT quote_ident(n.nspname) INTO s FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'vector';
    IF s IS NULL THEN
        RAISE NOTICE 'pgvector is not enabled: no embedding tables';
        RETURN;
    END IF;
    EXECUTE format('CREATE TABLE message_embeddings (
        embedding_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        group_id bigint NOT NULL REFERENCES groups (group_id) ON DELETE CASCADE,
        account_id bigint REFERENCES telegram_accounts (account_id) ON DELETE CASCADE,
        message_id bigint NOT NULL,
        model text NOT NULL,
        purpose text NOT NULL CONSTRAINT message_embeddings_purpose_check CHECK (purpose IN (''newcomer'', ''decision'')),
        embedding %s.halfvec NOT NULL,
        embedded_at timestamp with time zone NOT NULL DEFAULT now(),
        CONSTRAINT message_embeddings_message_key UNIQUE (group_id, message_id, model))', s);
    EXECUTE 'CREATE INDEX message_embeddings_embedded_idx ON message_embeddings (embedded_at)';
    EXECUTE 'CREATE INDEX message_embeddings_account_idx ON message_embeddings (account_id)';
    EXECUTE format('CREATE TABLE media_vectors (
        media_unique_id text NOT NULL,
        model text NOT NULL,
        kind text NOT NULL,
        embedding %s.halfvec NOT NULL,
        embedded_at timestamp with time zone NOT NULL DEFAULT now(),
        PRIMARY KEY (media_unique_id, model))', s);
    EXECUTE 'CREATE INDEX media_vectors_embedded_idx ON media_vectors (embedded_at)';
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS media_vectors;
DROP TABLE IF EXISTS message_embeddings;");
        }
    }
}
