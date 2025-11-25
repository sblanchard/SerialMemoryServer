"""MCP server for CORE-like temporal knowledge graph memory system."""

import asyncio
import logging
import sys
from pathlib import Path
from uuid import UUID

# Add parent directory to path for imports when running as script
if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).parent.parent))

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import (
    Tool,
    TextContent,
    Resource,
    INVALID_PARAMS,
    INTERNAL_ERROR,
)

try:
    from .config import settings
    from .db.postgres import db_pool
    from .services.embedding_service import embedding_service
    from .services.entity_extraction_service import entity_service
    from .services.knowledge_graph_service import kg_service
except ImportError:
    # Running as script, use absolute imports
    from src.config import settings
    from src.db.postgres import db_pool
    from src.services.embedding_service import embedding_service
    from src.services.entity_extraction_service import entity_service
    from src.services.knowledge_graph_service import kg_service

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
)
logger = logging.getLogger(__name__)

# Create MCP server instance
app = Server("serial-memory-server")

# Current conversation session (global state)
current_session_id: UUID | None = None


@app.list_tools()
async def list_tools() -> list[Tool]:
    """List available MCP tools."""
    return [
        Tool(
            name="memory_search",
            description=(
                "Search for relevant memories using semantic search, full-text search, or both. "
                "Returns memories with entities and temporal context. Supports multi-hop reasoning."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "description": "Search query (natural language)",
                    },
                    "mode": {
                        "type": "string",
                        "enum": ["semantic", "text", "hybrid"],
                        "default": "hybrid",
                        "description": "Search mode: semantic (vector), text (full-text), or hybrid",
                    },
                    "limit": {
                        "type": "integer",
                        "default": 10,
                        "description": "Maximum number of results to return",
                    },
                    "threshold": {
                        "type": "number",
                        "default": 0.7,
                        "description": "Minimum similarity threshold (0.0-1.0) for semantic search",
                    },
                    "include_entities": {
                        "type": "boolean",
                        "default": True,
                        "description": "Include linked entities in results",
                    },
                },
                "required": ["query"],
            },
        ),
        Tool(
            name="memory_ingest",
            description=(
                "Add a new memory (episode) to the knowledge graph. "
                "Automatically extracts entities, relationships, and generates embeddings. "
                "Links memory to current conversation session if active."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "content": {
                        "type": "string",
                        "description": "Memory content to store",
                    },
                    "source": {
                        "type": "string",
                        "description": "Source of the memory (e.g., 'claude-desktop', 'cursor')",
                    },
                    "metadata": {
                        "type": "object",
                        "description": "Additional metadata (tags, importance, etc.)",
                    },
                    "extract_entities": {
                        "type": "boolean",
                        "default": True,
                        "description": "Whether to extract entities and relationships",
                    },
                },
                "required": ["content"],
            },
        ),
        Tool(
            name="memory_about_user",
            description=(
                "Retrieve structured information about the user's persona, preferences, "
                "skills, goals, and background learned from previous interactions."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "user_id": {
                        "type": "string",
                        "default": "default_user",
                        "description": "User identifier",
                    },
                },
            },
        ),
        Tool(
            name="initialise_conversation_session",
            description=(
                "Create a new conversation session to track context across interactions. "
                "Subsequent memories will be linked to this session."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "session_name": {
                        "type": "string",
                        "description": "Optional session name/title",
                    },
                    "client_type": {
                        "type": "string",
                        "description": "Client type (e.g., 'claude-desktop', 'cursor', 'windsurf')",
                    },
                    "metadata": {
                        "type": "object",
                        "description": "Additional session metadata",
                    },
                },
            },
        ),
        Tool(
            name="end_conversation_session",
            description="End the current conversation session.",
            inputSchema={
                "type": "object",
                "properties": {},
            },
        ),
        Tool(
            name="get_integrations",
            description="List available integrations (external tools/APIs).",
            inputSchema={
                "type": "object",
                "properties": {},
            },
        ),
        Tool(
            name="memory_multi_hop_search",
            description=(
                "Perform multi-hop reasoning by traversing the knowledge graph. "
                "Finds initial memories, then follows entity relationships to discover "
                "connected memories and relationships."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "query": {
                        "type": "string",
                        "description": "Initial search query",
                    },
                    "hops": {
                        "type": "integer",
                        "default": 2,
                        "description": "Number of relationship hops to traverse",
                    },
                    "max_results_per_hop": {
                        "type": "integer",
                        "default": 5,
                        "description": "Maximum results per hop",
                    },
                },
                "required": ["query"],
            },
        ),
    ]


@app.call_tool()
async def call_tool(name: str, arguments: dict) -> list[TextContent]:
    """Handle MCP tool calls."""
    global current_session_id

    try:
        if name == "memory_search":
            query = arguments.get("query")
            if not query:
                raise ValueError("query is required")

            mode = arguments.get("mode", "hybrid")
            limit = arguments.get("limit", 10)
            threshold = arguments.get("threshold", 0.7)
            include_entities = arguments.get("include_entities", True)

            results = await kg_service.search_memories(
                query=query,
                mode=mode,
                limit=limit,
                threshold=threshold,
                include_entities=include_entities,
            )

            return [
                TextContent(
                    type="text",
                    text=f"Found {len(results)} memories:\n\n"
                    + "\n\n".join(
                        [
                            f"**Memory {i+1}** (ID: {r['id']})\n"
                            f"Created: {r['created_at']}\n"
                            f"Content: {r['content']}\n"
                            f"Entities: {', '.join([e['name'] for e in r.get('entities', [])])}\n"
                            f"Similarity: {r.get('similarity', r.get('rank', 'N/A'))}"
                            for i, r in enumerate(results)
                        ]
                    ),
                )
            ]

        elif name == "memory_ingest":
            content = arguments.get("content")
            if not content:
                raise ValueError("content is required")

            source = arguments.get("source")
            metadata = arguments.get("metadata")
            extract_entities = arguments.get("extract_entities", True)

            result = await kg_service.ingest_memory(
                content=content,
                source=source,
                session_id=current_session_id,
                metadata=metadata,
                extract_entities=extract_entities,
            )

            return [
                TextContent(
                    type="text",
                    text=(
                        f"Memory ingested successfully!\n\n"
                        f"Memory ID: {result['memory_id']}\n"
                        f"Entities extracted: {result['entities_created']}\n"
                        f"Relationships extracted: {result['relationships_created']}\n\n"
                        f"Entities: {', '.join([e['name'] for e in result['entities']])}\n"
                        f"Relationships: {', '.join([r['source'] + ' --' + r['type'] + '--> ' + r['target'] for r in result['relationships']])}"
                    ),
                )
            ]

        elif name == "memory_about_user":
            user_id = arguments.get("user_id", "default_user")
            persona = await kg_service.get_user_persona(user_id)

            if not persona:
                return [
                    TextContent(
                        type="text",
                        text=f"No persona information found for user: {user_id}",
                    )
                ]

            # Format persona nicely
            formatted = f"User Persona for {user_id}:\n\n"
            for attr_type, attributes in persona.items():
                formatted += f"**{attr_type.title()}:**\n"
                for key, value_data in attributes.items():
                    formatted += f"  - {key}: {value_data['value']} (confidence: {value_data['confidence']:.2f})\n"
                formatted += "\n"

            return [TextContent(type="text", text=formatted)]

        elif name == "initialise_conversation_session":
            session_name = arguments.get("session_name")
            client_type = arguments.get("client_type")
            metadata = arguments.get("metadata")

            session_id = await kg_service.db.create_conversation_session(
                session_name=session_name,
                client_type=client_type,
                metadata=metadata,
            )

            current_session_id = session_id

            return [
                TextContent(
                    type="text",
                    text=f"Conversation session initialized: {session_id}",
                )
            ]

        elif name == "end_conversation_session":
            if current_session_id:
                await kg_service.db.end_conversation_session(current_session_id)
                old_session = current_session_id
                current_session_id = None
                return [
                    TextContent(
                        type="text",
                        text=f"Conversation session ended: {old_session}",
                    )
                ]
            else:
                return [
                    TextContent(
                        type="text",
                        text="No active conversation session to end.",
                    )
                ]

        elif name == "get_integrations":
            # Placeholder - return empty list for now
            return [
                TextContent(
                    type="text",
                    text="No integrations configured yet.",
                )
            ]

        elif name == "memory_multi_hop_search":
            query = arguments.get("query")
            if not query:
                raise ValueError("query is required")

            hops = arguments.get("hops", 2)
            max_results_per_hop = arguments.get("max_results_per_hop", 5)

            result = await kg_service.multi_hop_search(
                start_query=query,
                hops=hops,
                max_results_per_hop=max_results_per_hop,
            )

            return [
                TextContent(
                    type="text",
                    text=(
                        f"Multi-hop search completed ({result['hops']} hops):\n\n"
                        f"Memories found: {len(result['memories'])}\n"
                        f"Entities discovered: {len(result['entities'])}\n"
                        f"Relationships: {len(result['relationships'])}\n\n"
                        + "\n\n".join(
                            [
                                f"**Memory {i+1}:**\n{m['content'][:200]}..."
                                for i, m in enumerate(result["memories"][:5])
                            ]
                        )
                    ),
                )
            ]

        else:
            raise ValueError(f"Unknown tool: {name}")

    except ValueError as e:
        return [TextContent(type="text", text=f"Error: {str(e)}")]
    except Exception as e:
        logger.error(f"Error executing tool {name}: {e}", exc_info=True)
        return [TextContent(type="text", text=f"Internal error: {str(e)}")]


@app.list_resources()
async def list_resources() -> list[Resource]:
    """List available MCP resources."""
    return [
        Resource(
            uri="memory://recent",
            name="Recent Memories",
            mimeType="application/json",
            description="List of recently added memories",
        ),
        Resource(
            uri="memory://sessions",
            name="Conversation Sessions",
            mimeType="application/json",
            description="List of recent conversation sessions",
        ),
    ]


@app.read_resource()
async def read_resource(uri: str) -> str:
    """Read MCP resource."""
    try:
        if uri == "memory://recent":
            # Get recent memories
            results = await kg_service.search_memories(
                query="",  # Empty query returns recent
                mode="text",
                limit=20,
            )
            import json

            return json.dumps(results, indent=2)

        elif uri == "memory://sessions":
            # Get recent sessions
            sessions = await kg_service.db.get_recent_sessions(limit=20)
            import json

            # Convert UUIDs and timestamps to strings
            for session in sessions:
                session["id"] = str(session["id"])
                session["started_at"] = session["started_at"].isoformat()
                if session["ended_at"]:
                    session["ended_at"] = session["ended_at"].isoformat()

            return json.dumps(sessions, indent=2)

        else:
            raise ValueError(f"Unknown resource: {uri}")

    except Exception as e:
        logger.error(f"Error reading resource {uri}: {e}", exc_info=True)
        raise


async def main():
    """Main entry point for the MCP server."""
    logger.info("Starting Serial Memory MCP Server (CORE-like)")
    logger.info(f"Database: {settings.database_url}")
    logger.info(f"Embedding model: {settings.embedding_model}")
    logger.info(f"spaCy model: {settings.spacy_model}")

    # Initialize services
    try:
        logger.info("Initializing database pool...")
        await db_pool.initialize()

        logger.info("Initializing embedding service...")
        await embedding_service.initialize()

        logger.info("Initializing entity extraction service...")
        await entity_service.initialize()

        logger.info("All services initialized successfully")

        # Run MCP server
        async with stdio_server() as (read_stream, write_stream):
            await app.run(read_stream, write_stream, app.create_initialization_options())

    except Exception as e:
        logger.error(f"Fatal error: {e}", exc_info=True)
        raise
    finally:
        logger.info("Shutting down...")
        await db_pool.close()


if __name__ == "__main__":
    # Windows-specific: psycopg requires SelectorEventLoop
    import platform
    if platform.system() == "Windows":
        import selectors
        asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

    asyncio.run(main())
