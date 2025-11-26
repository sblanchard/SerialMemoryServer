#!/usr/bin/env python3
"""
HTTP Entity/Relationship Extraction Service using Ollama

Provides HTTP endpoints for extracting entities and relationships from text
using a local LLM via Ollama.

Usage:
    python extraction_http_service.py [--port PORT] [--model MODEL]

Default:
    Port: 8766
    Model: phi3

Endpoints:
    POST /extract - Extract entities and relationships from text
        Request: {"text": "your text here"}
        Response: {
            "entities": [{"text": "...", "label": "...", "confidence": 0.9}],
            "relationships": [{"source": "...", "target": "...", "type": "...", "confidence": 0.8}]
        }

    GET /health - Health check
        Response: {"status": "ok", "model": "phi3"}
"""

import argparse
import json
import logging
import re
from typing import List, Dict, Any

import httpx
import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s"
)
logger = logging.getLogger(__name__)

# FastAPI app
app = FastAPI(
    title="SerialMemory Entity Extraction Service",
    description="HTTP service for extracting entities and relationships using Ollama",
    version="1.0.0"
)

# Global config
ollama_url: str = "http://localhost:11434"
model_name: str = "phi3"


class ExtractRequest(BaseModel):
    text: str


class Entity(BaseModel):
    text: str
    label: str
    confidence: float = 0.9


class Relationship(BaseModel):
    source: str
    target: str
    type: str
    confidence: float = 0.8


class ExtractResponse(BaseModel):
    entities: List[Entity]
    relationships: List[Relationship]


class HealthResponse(BaseModel):
    status: str
    model: str


EXTRACTION_PROMPT = """Extract named entities and relationships from the following text.

Return a JSON object with two arrays:
1. "entities": array of objects with "text" (the entity), "label" (type: PERSON, ORG, PRODUCT, SOFTWARE, GPE, DATE, TITLE), "confidence" (0.0-1.0)
2. "relationships": array of objects with "source" (entity), "target" (entity), "type" (relationship like WORKS_ON, USES, INTEGRATES_WITH, WORKS_AT, KNOWS, CREATED), "confidence" (0.0-1.0)

Focus on:
- People (PERSON): names like "John Smith", "Stephan"
- Organizations (ORG): companies, teams
- Products/Projects (PRODUCT): FlexRadio, SmartSDR, PostgreSQL, Redis, etc.
- Software Components (SOFTWARE): APIs, servers, services
- Locations (GPE): cities, countries
- Technical relationships: who works on what, what integrates with what, what uses what

TEXT:
{text}

Return ONLY valid JSON, no other text:"""


async def call_ollama(prompt: str) -> str:
    """Call Ollama API to generate a response."""
    async with httpx.AsyncClient(timeout=60.0) as client:
        response = await client.post(
            f"{ollama_url}/api/generate",
            json={
                "model": model_name,
                "prompt": prompt,
                "stream": False,
                "options": {
                    "temperature": 0.1,  # Low temperature for consistent extraction
                    "num_predict": 1024
                }
            }
        )
        response.raise_for_status()
        return response.json()["response"]


def parse_extraction_response(response: str) -> tuple[List[Dict], List[Dict]]:
    """Parse the LLM response into entities and relationships."""
    entities = []
    relationships = []

    # Try to extract JSON from the response
    try:
        # Find JSON in the response (might be wrapped in markdown code blocks)
        json_match = re.search(r'\{[\s\S]*\}', response)
        if json_match:
            data = json.loads(json_match.group())
            entities = data.get("entities", [])
            relationships = data.get("relationships", [])
    except json.JSONDecodeError as e:
        logger.warning(f"Failed to parse JSON response: {e}")
        logger.debug(f"Raw response: {response}")

    return entities, relationships


@app.get("/health", response_model=HealthResponse)
async def health():
    """Health check endpoint."""
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.get(f"{ollama_url}/api/tags")
            response.raise_for_status()
        return HealthResponse(status="ok", model=model_name)
    except Exception as e:
        raise HTTPException(status_code=503, detail=f"Ollama not available: {e}")


@app.post("/extract", response_model=ExtractResponse)
async def extract(request: ExtractRequest):
    """Extract entities and relationships from text."""
    if not request.text or len(request.text.strip()) < 10:
        return ExtractResponse(entities=[], relationships=[])

    try:
        prompt = EXTRACTION_PROMPT.format(text=request.text[:2000])  # Limit text length
        response = await call_ollama(prompt)

        raw_entities, raw_relationships = parse_extraction_response(response)

        # Validate and convert entities
        entities = []
        for e in raw_entities:
            if isinstance(e, dict) and "text" in e and "label" in e:
                entities.append(Entity(
                    text=str(e["text"]),
                    label=str(e.get("label", "UNKNOWN")).upper(),
                    confidence=float(e.get("confidence", 0.9))
                ))

        # Validate and convert relationships
        relationships = []
        for r in raw_relationships:
            if isinstance(r, dict) and "source" in r and "target" in r and "type" in r:
                relationships.append(Relationship(
                    source=str(r["source"]),
                    target=str(r["target"]),
                    type=str(r.get("type", "RELATED_TO")).upper(),
                    confidence=float(r.get("confidence", 0.8))
                ))

        logger.info(f"Extracted {len(entities)} entities and {len(relationships)} relationships")
        return ExtractResponse(entities=entities, relationships=relationships)

    except httpx.HTTPError as e:
        logger.error(f"Ollama API error: {e}")
        raise HTTPException(status_code=503, detail=f"Ollama API error: {e}")
    except Exception as e:
        logger.error(f"Extraction error: {e}")
        raise HTTPException(status_code=500, detail=str(e))


def main():
    global model_name, ollama_url

    parser = argparse.ArgumentParser(description="SerialMemory Entity Extraction HTTP Service")
    parser.add_argument(
        "--port",
        type=int,
        default=8766,
        help="Port to run the service on (default: 8766)"
    )
    parser.add_argument(
        "--model",
        type=str,
        default="phi3",
        help="Ollama model to use (default: phi3)"
    )
    parser.add_argument(
        "--ollama-url",
        type=str,
        default="http://localhost:11434",
        help="Ollama API URL (default: http://localhost:11434)"
    )
    parser.add_argument(
        "--host",
        type=str,
        default="0.0.0.0",
        help="Host to bind to (default: 0.0.0.0)"
    )

    args = parser.parse_args()
    model_name = args.model
    ollama_url = args.ollama_url

    logger.info(f"Starting extraction service on {args.host}:{args.port}")
    logger.info(f"Using Ollama model: {model_name} at {ollama_url}")

    uvicorn.run(
        app,
        host=args.host,
        port=args.port,
        log_level="info"
    )


if __name__ == "__main__":
    main()
