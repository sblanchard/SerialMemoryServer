"""PostgreSQL database connection and operations."""

import logging
from contextlib import asynccontextmanager
from typing import Any, AsyncGenerator
from uuid import UUID

import psycopg
from psycopg.rows import dict_row
from psycopg_pool import AsyncConnectionPool
from pgvector.psycopg import register_vector_async

from ..config import settings

logger = logging.getLogger(__name__)


class DatabasePool:
    """Manages async PostgreSQL connection pool."""

    def __init__(self):
        self._pool: AsyncConnectionPool | None = None

    async def initialize(self):
        """Initialize the connection pool."""
        self._pool = AsyncConnectionPool(
            conninfo=settings.database_url,
            min_size=2,
            max_size=10,
            timeout=30,
            max_idle=300,
            open=False,
        )
        await self._pool.open()

        # Register pgvector types
        async with self._pool.connection() as conn:
            await register_vector_async(conn)

        logger.info("Database pool initialized")

    async def close(self):
        """Close the connection pool."""
        if self._pool:
            await self._pool.close()
            logger.info("Database pool closed")

    @asynccontextmanager
    async def connection(self) -> AsyncGenerator[psycopg.AsyncConnection, None]:
        """Get a connection from the pool."""
        if not self._pool:
            raise RuntimeError("Database pool not initialized")

        async with self._pool.connection() as conn:
            conn.row_factory = dict_row
            yield conn


# Global database pool instance
db_pool = DatabasePool()


class KnowledgeGraphDB:
    """Knowledge graph database operations."""

    def __init__(self, pool: DatabasePool):
        self.pool = pool

    async def create_memory(
        self,
        content: str,
        embedding: list[float],
        source: str | None = None,
        session_id: UUID | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> UUID:
        """Create a new memory record."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                INSERT INTO memories (content, embedding, source, conversation_session_id, metadata)
                VALUES (%s, %s, %s, %s, %s)
                RETURNING id
                """,
                (content, embedding, source, session_id, metadata),
            )
            row = await result.fetchone()
            return row["id"]

    async def search_memories_by_embedding(
        self, query_embedding: list[float], limit: int = 10, threshold: float = 0.7
    ) -> list[dict[str, Any]]:
        """Search memories by semantic similarity using cosine distance."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                SELECT
                    id, content, created_at, source, metadata,
                    1 - (embedding <=> %s) as similarity
                FROM memories
                WHERE 1 - (embedding <=> %s) > %s
                ORDER BY embedding <=> %s
                LIMIT %s
                """,
                (query_embedding, query_embedding, threshold, query_embedding, limit),
            )
            return await result.fetchall()

    async def search_memories_by_text(
        self, query: str, limit: int = 10
    ) -> list[dict[str, Any]]:
        """Search memories using full-text search."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                SELECT
                    id, content, created_at, source, metadata,
                    ts_rank(content_tsvector, plainto_tsquery('english', %s)) as rank
                FROM memories
                WHERE content_tsvector @@ plainto_tsquery('english', %s)
                ORDER BY rank DESC
                LIMIT %s
                """,
                (query, query, limit),
            )
            return await result.fetchall()

    async def get_memory_by_id(self, memory_id: UUID) -> dict[str, Any] | None:
        """Get a specific memory by ID."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                "SELECT * FROM memories WHERE id = %s",
                (memory_id,),
            )
            return await result.fetchone()

    async def create_entity(
        self,
        name: str,
        entity_type: str,
        canonical_name: str | None = None,
        first_seen_memory_id: UUID | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> UUID:
        """Create or get an entity."""
        async with self.pool.connection() as conn:
            # Try to insert, return existing if conflict
            result = await conn.execute(
                """
                INSERT INTO entities (name, entity_type, canonical_name, first_seen_memory_id, metadata)
                VALUES (%s, %s, %s, %s, %s)
                ON CONFLICT (name, entity_type) DO UPDATE SET
                    canonical_name = COALESCE(EXCLUDED.canonical_name, entities.canonical_name),
                    metadata = COALESCE(EXCLUDED.metadata, entities.metadata)
                RETURNING id
                """,
                (name, entity_type, canonical_name, first_seen_memory_id, metadata),
            )
            row = await result.fetchone()
            return row["id"]

    async def link_memory_to_entity(
        self, memory_id: UUID, entity_id: UUID, relevance: float = 1.0
    ):
        """Link a memory to an entity."""
        async with self.pool.connection() as conn:
            await conn.execute(
                """
                INSERT INTO memory_entities (memory_id, entity_id, relevance)
                VALUES (%s, %s, %s)
                ON CONFLICT (memory_id, entity_id) DO UPDATE SET relevance = EXCLUDED.relevance
                """,
                (memory_id, entity_id, relevance),
            )

    async def create_relationship(
        self,
        source_entity_id: UUID,
        target_entity_id: UUID,
        relationship_type: str,
        confidence: float = 1.0,
        first_seen_memory_id: UUID | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> UUID:
        """Create a relationship between entities."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                INSERT INTO entity_relationships
                    (source_entity_id, target_entity_id, relationship_type, confidence, first_seen_memory_id, metadata)
                VALUES (%s, %s, %s, %s, %s, %s)
                ON CONFLICT (source_entity_id, target_entity_id, relationship_type) DO UPDATE SET
                    confidence = GREATEST(entity_relationships.confidence, EXCLUDED.confidence),
                    metadata = COALESCE(EXCLUDED.metadata, entity_relationships.metadata)
                RETURNING id
                """,
                (
                    source_entity_id,
                    target_entity_id,
                    relationship_type,
                    confidence,
                    first_seen_memory_id,
                    metadata,
                ),
            )
            row = await result.fetchone()
            return row["id"]

    async def get_entities_for_memory(self, memory_id: UUID) -> list[dict[str, Any]]:
        """Get all entities linked to a memory."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                SELECT e.*, me.relevance
                FROM entities e
                JOIN memory_entities me ON e.id = me.entity_id
                WHERE me.memory_id = %s
                ORDER BY me.relevance DESC
                """,
                (memory_id,),
            )
            return await result.fetchall()

    async def get_relationships_for_entity(
        self, entity_id: UUID
    ) -> list[dict[str, Any]]:
        """Get all relationships for an entity."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                SELECT
                    er.*,
                    source.name as source_name,
                    source.entity_type as source_type,
                    target.name as target_name,
                    target.entity_type as target_type
                FROM entity_relationships er
                JOIN entities source ON er.source_entity_id = source.id
                JOIN entities target ON er.target_entity_id = target.id
                WHERE er.source_entity_id = %s OR er.target_entity_id = %s
                ORDER BY er.confidence DESC
                """,
                (entity_id, entity_id),
            )
            return await result.fetchall()

    async def set_user_persona_attribute(
        self,
        attribute_type: str,
        attribute_key: str,
        attribute_value: str,
        user_id: str = "default_user",
        confidence: float = 1.0,
        source_memory_id: UUID | None = None,
    ):
        """Set or update a user persona attribute."""
        async with self.pool.connection() as conn:
            await conn.execute(
                """
                INSERT INTO user_personas
                    (user_id, attribute_type, attribute_key, attribute_value, confidence, source_memory_id)
                VALUES (%s, %s, %s, %s, %s, %s)
                ON CONFLICT (user_id, attribute_type, attribute_key) DO UPDATE SET
                    attribute_value = EXCLUDED.attribute_value,
                    confidence = EXCLUDED.confidence,
                    updated_at = NOW()
                """,
                (
                    user_id,
                    attribute_type,
                    attribute_key,
                    attribute_value,
                    confidence,
                    source_memory_id,
                ),
            )

    async def get_user_persona(self, user_id: str = "default_user") -> dict[str, Any]:
        """Get all persona attributes for a user."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                SELECT attribute_type, attribute_key, attribute_value, confidence, updated_at
                FROM user_personas
                WHERE user_id = %s
                ORDER BY attribute_type, attribute_key
                """,
                (user_id,),
            )
            rows = await result.fetchall()

            # Group by attribute_type
            persona = {}
            for row in rows:
                attr_type = row["attribute_type"]
                if attr_type not in persona:
                    persona[attr_type] = {}
                persona[attr_type][row["attribute_key"]] = {
                    "value": row["attribute_value"],
                    "confidence": row["confidence"],
                    "updated_at": row["updated_at"].isoformat(),
                }

            return persona

    async def create_conversation_session(
        self,
        session_name: str | None = None,
        client_type: str | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> UUID:
        """Create a new conversation session."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                INSERT INTO conversation_sessions (session_name, client_type, metadata)
                VALUES (%s, %s, %s)
                RETURNING id
                """,
                (session_name, client_type, metadata),
            )
            row = await result.fetchone()
            return row["id"]

    async def end_conversation_session(self, session_id: UUID):
        """Mark a conversation session as ended."""
        async with self.pool.connection() as conn:
            await conn.execute(
                "UPDATE conversation_sessions SET ended_at = NOW() WHERE id = %s",
                (session_id,),
            )

    async def get_recent_sessions(self, limit: int = 10) -> list[dict[str, Any]]:
        """Get recent conversation sessions."""
        async with self.pool.connection() as conn:
            result = await conn.execute(
                """
                SELECT * FROM conversation_sessions
                ORDER BY started_at DESC
                LIMIT %s
                """,
                (limit,),
            )
            return await result.fetchall()
