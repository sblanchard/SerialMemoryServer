"""Test MCP tools manually."""
import asyncio
import sys
import os
from pathlib import Path

# Add project to path
sys.path.insert(0, str(Path(__file__).parent / "SerialMemory.Mcp.Python"))

# Set environment variables
os.environ["POSTGRES_HOST"] = "localhost"
os.environ["POSTGRES_PORT"] = "5434"
os.environ["POSTGRES_USER"] = "postgres"
os.environ["POSTGRES_PASSWORD"] = "postgres"
os.environ["POSTGRES_DB"] = "contextdb"

from src.config import settings
from src.db.postgres import db_pool
from src.services.embedding_service import embedding_service
from src.services.entity_extraction_service import entity_service
from src.services.knowledge_graph_service import kg_service


async def test_memory_ingest():
    """Test ingesting a memory."""
    print("Testing memory ingestion...")

    # Initialize services
    print("Initializing database pool...")
    await db_pool.initialize()

    print("Initializing embedding service...")
    await embedding_service.initialize()

    print("Initializing entity extraction service...")
    await entity_service.initialize()

    print("\nServices initialized successfully!\n")

    # Test memory ingestion
    test_content = "I'm learning .NET microservices with MassTransit, RabbitMQ, and Redis for job interviews."

    print(f"Ingesting memory: {test_content}")
    result = await kg_service.ingest_memory(
        content=test_content,
        source="test-script",
        extract_entities=True
    )

    print(f"\n✅ Memory ingested successfully!")
    print(f"Memory ID: {result['memory_id']}")
    print(f"Entities created: {result['entities_created']}")
    print(f"Relationships created: {result['relationships_created']}")
    print(f"\nExtracted entities:")
    for entity in result['entities']:
        print(f"  - {entity['name']} ({entity['type']})")

    print(f"\nRelationships:")
    for rel in result['relationships']:
        print(f"  - {rel['source']} --{rel['type']}--> {rel['target']}")

    # Close connection
    await db_pool.close()
    print("\n✅ Test completed!")


if __name__ == "__main__":
    # Windows-specific: psycopg requires SelectorEventLoop
    import platform
    if platform.system() == "Windows":
        asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

    asyncio.run(test_memory_ingest())
