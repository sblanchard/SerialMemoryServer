"""Knowledge graph service orchestrating all operations."""

import logging
from typing import Any, List
from uuid import UUID

from .embedding_service import embedding_service
from .entity_extraction_service import entity_service
from ..db.postgres import KnowledgeGraphDB, db_pool

logger = logging.getLogger(__name__)


class KnowledgeGraphService:
    """Orchestrates knowledge graph operations."""

    def __init__(self):
        self.db = KnowledgeGraphDB(db_pool)
        self.embedding_service = embedding_service
        self.entity_service = entity_service

    async def ingest_memory(
        self,
        content: str,
        source: str | None = None,
        session_id: UUID | None = None,
        metadata: dict[str, Any] | None = None,
        extract_entities: bool = True,
    ) -> dict[str, Any]:
        """
        Ingest a new memory with full knowledge graph processing.

        Returns a dict with memory_id, entities, and relationships.
        """
        # Generate embedding
        embedding = self.embedding_service.embed_text(content)

        # Create memory record
        memory_id = await self.db.create_memory(
            content=content,
            embedding=embedding,
            source=source,
            session_id=session_id,
            metadata=metadata,
        )

        logger.info(f"Created memory {memory_id}")

        entities_created = []
        relationships_created = []

        # Extract entities and relationships if requested
        if extract_entities:
            entities, relationships = self.entity_service.extract_entities_and_relationships(
                content
            )

            logger.info(
                f"Extracted {len(entities)} entities and {len(relationships)} relationships"
            )

            # Store entities and link to memory
            entity_map = {}  # name -> entity_id
            for entity in entities:
                entity_id = await self.db.create_entity(
                    name=entity.text,
                    entity_type=entity.label,
                    first_seen_memory_id=memory_id,
                )
                entity_map[entity.text] = entity_id

                await self.db.link_memory_to_entity(
                    memory_id=memory_id,
                    entity_id=entity_id,
                    relevance=entity.confidence,
                )

                entities_created.append(
                    {"id": str(entity_id), "name": entity.text, "type": entity.label}
                )

            # Store relationships
            for rel in relationships:
                if rel.source_entity in entity_map and rel.target_entity in entity_map:
                    rel_id = await self.db.create_relationship(
                        source_entity_id=entity_map[rel.source_entity],
                        target_entity_id=entity_map[rel.target_entity],
                        relationship_type=rel.relation_type,
                        confidence=rel.confidence,
                        first_seen_memory_id=memory_id,
                    )

                    relationships_created.append(
                        {
                            "id": str(rel_id),
                            "source": rel.source_entity,
                            "target": rel.target_entity,
                            "type": rel.relation_type,
                        }
                    )

        return {
            "memory_id": str(memory_id),
            "entities_created": len(entities_created),
            "relationships_created": len(relationships_created),
            "entities": entities_created,
            "relationships": relationships_created,
        }

    async def search_memories(
        self,
        query: str,
        mode: str = "hybrid",  # "semantic", "text", or "hybrid"
        limit: int = 10,
        threshold: float = 0.7,
        include_entities: bool = True,
    ) -> List[dict[str, Any]]:
        """
        Search memories using semantic and/or text search.

        Args:
            query: Search query
            mode: "semantic" (vector), "text" (full-text), or "hybrid" (both)
            limit: Maximum number of results
            threshold: Minimum similarity threshold for semantic search
            include_entities: Whether to include linked entities in results

        Returns:
            List of memory records with metadata
        """
        results = []

        if mode in ("semantic", "hybrid"):
            # Generate query embedding
            query_embedding = self.embedding_service.embed_text(query)

            # Semantic search
            semantic_results = await self.db.search_memories_by_embedding(
                query_embedding=query_embedding,
                limit=limit,
                threshold=threshold,
            )
            results.extend(semantic_results)

        if mode in ("text", "hybrid"):
            # Full-text search
            text_results = await self.db.search_memories_by_text(
                query=query,
                limit=limit,
            )
            results.extend(text_results)

        # Deduplicate by memory ID if hybrid mode
        if mode == "hybrid":
            seen = set()
            unique_results = []
            for result in results:
                if result["id"] not in seen:
                    seen.add(result["id"])
                    unique_results.append(result)
            results = unique_results[:limit]

        # Enrich with entities if requested
        if include_entities:
            for result in results:
                entities = await self.db.get_entities_for_memory(result["id"])
                result["entities"] = [
                    {
                        "name": e["name"],
                        "type": e["entity_type"],
                        "relevance": e["relevance"],
                    }
                    for e in entities
                ]

        # Convert UUIDs and timestamps to strings for JSON serialization
        for result in results:
            result["id"] = str(result["id"])
            result["created_at"] = result["created_at"].isoformat()
            if result.get("conversation_session_id"):
                result["conversation_session_id"] = str(
                    result["conversation_session_id"]
                )

        return results

    async def get_user_persona(self, user_id: str = "default_user") -> dict[str, Any]:
        """Get structured user persona information."""
        return await self.db.get_user_persona(user_id)

    async def update_user_persona_from_memory(
        self, content: str, user_id: str = "default_user"
    ) -> dict[str, Any]:
        """
        Extract user preferences/attributes from memory content and update persona.

        This is a simple implementation. For production, you might want to use
        an LLM to better understand user attributes.
        """
        # Simple keyword-based extraction
        updates = []

        # Extract preferences
        if "I prefer" in content or "I like" in content:
            # Simple extraction - in production use NLP/LLM
            await self.db.set_user_persona_attribute(
                user_id=user_id,
                attribute_type="preference",
                attribute_key="extracted_from_text",
                attribute_value=content[:200],  # Truncate
                confidence=0.7,
            )
            updates.append("preference")

        # Extract skills
        if "I can" in content or "I know" in content:
            await self.db.set_user_persona_attribute(
                user_id=user_id,
                attribute_type="skill",
                attribute_key="extracted_from_text",
                attribute_value=content[:200],
                confidence=0.7,
            )
            updates.append("skill")

        return {"updates": updates}

    async def multi_hop_search(
        self,
        start_query: str,
        hops: int = 2,
        max_results_per_hop: int = 5,
    ) -> dict[str, Any]:
        """
        Perform multi-hop reasoning by traversing the knowledge graph.

        1. Find initial memories matching the query
        2. Extract entities from those memories
        3. Find relationships involving those entities
        4. Find memories linked to related entities

        Returns a structured graph of connected memories, entities, and relationships.
        """
        # Initial search
        initial_memories = await self.search_memories(
            query=start_query,
            mode="hybrid",
            limit=max_results_per_hop,
        )

        if not initial_memories:
            return {"memories": [], "entities": [], "relationships": [], "hops": 0}

        # Track discovered items
        all_memories = {m["id"]: m for m in initial_memories}
        all_entities = {}
        all_relationships = []

        # Extract entities from initial memories
        for memory in initial_memories:
            if "entities" in memory:
                for entity in memory["entities"]:
                    entity_name = entity["name"]
                    if entity_name not in all_entities:
                        all_entities[entity_name] = entity

        # Perform hops
        for hop_num in range(hops):
            logger.info(f"Performing hop {hop_num + 1}/{hops}")

            # For each entity, find relationships
            for entity_name in list(all_entities.keys()):
                # Get entity ID (simplified - in production cache this)
                # This is a placeholder - you'd need to look up entity by name
                # For now, we'll just log
                logger.debug(f"Would traverse from entity: {entity_name}")

        return {
            "memories": list(all_memories.values()),
            "entities": list(all_entities.values()),
            "relationships": all_relationships,
            "hops": hops,
        }


# Global knowledge graph service instance
kg_service = KnowledgeGraphService()
