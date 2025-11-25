#!/usr/bin/env python3
"""
HTTP Embedding Service for SerialMemory C# MCP Server

This service provides HTTP endpoints for generating text embeddings
using sentence-transformers. The C# MCP server calls this service
to generate embeddings for memory ingestion and search.

Usage:
    python embedding_http_service.py [--port PORT] [--model MODEL]

Default:
    Port: 8765
    Model: sentence-transformers/all-MiniLM-L6-v2 (384-dim vectors)

Endpoints:
    POST /embed - Generate embedding for single text
        Request: {"text": "your text here"}
        Response: {"embedding": [0.1, 0.2, ...]}

    POST /embed-batch - Generate embeddings for multiple texts
        Request: {"texts": ["text1", "text2", ...]}
        Response: {"embeddings": [[0.1, ...], [0.2, ...], ...]}

    GET /health - Health check
        Response: {"status": "ok", "model": "...", "dimension": 384}
"""

import argparse
import logging
from typing import List

import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from sentence_transformers import SentenceTransformer

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s"
)
logger = logging.getLogger(__name__)

# FastAPI app
app = FastAPI(
    title="SerialMemory Embedding Service",
    description="HTTP service for generating text embeddings",
    version="1.0.0"
)

# Global model instance
model: SentenceTransformer = None
model_name: str = None


class EmbedRequest(BaseModel):
    text: str


class EmbedBatchRequest(BaseModel):
    texts: List[str]


class EmbedResponse(BaseModel):
    embedding: List[float]


class EmbedBatchResponse(BaseModel):
    embeddings: List[List[float]]


class HealthResponse(BaseModel):
    status: str
    model: str
    dimension: int


@app.on_event("startup")
async def startup_event():
    """Load model on startup."""
    global model, model_name
    logger.info(f"Loading model: {model_name}")
    model = SentenceTransformer(model_name)
    logger.info(f"Model loaded. Embedding dimension: {model.get_sentence_embedding_dimension()}")


@app.get("/health", response_model=HealthResponse)
async def health():
    """Health check endpoint."""
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")
    return HealthResponse(
        status="ok",
        model=model_name,
        dimension=model.get_sentence_embedding_dimension()
    )


@app.post("/embed", response_model=EmbedResponse)
async def embed(request: EmbedRequest):
    """Generate embedding for a single text."""
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")

    try:
        embedding = model.encode(request.text, convert_to_numpy=True)
        return EmbedResponse(embedding=embedding.tolist())
    except Exception as e:
        logger.error(f"Error generating embedding: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/embed-batch", response_model=EmbedBatchResponse)
async def embed_batch(request: EmbedBatchRequest):
    """Generate embeddings for multiple texts."""
    if model is None:
        raise HTTPException(status_code=503, detail="Model not loaded")

    if not request.texts:
        return EmbedBatchResponse(embeddings=[])

    try:
        embeddings = model.encode(request.texts, convert_to_numpy=True)
        return EmbedBatchResponse(embeddings=[e.tolist() for e in embeddings])
    except Exception as e:
        logger.error(f"Error generating batch embeddings: {e}")
        raise HTTPException(status_code=500, detail=str(e))


def main():
    global model_name

    parser = argparse.ArgumentParser(description="SerialMemory Embedding HTTP Service")
    parser.add_argument(
        "--port",
        type=int,
        default=8765,
        help="Port to run the service on (default: 8765)"
    )
    parser.add_argument(
        "--model",
        type=str,
        default="sentence-transformers/all-MiniLM-L6-v2",
        help="Sentence transformers model to use (default: all-MiniLM-L6-v2)"
    )
    parser.add_argument(
        "--host",
        type=str,
        default="0.0.0.0",
        help="Host to bind to (default: 0.0.0.0)"
    )

    args = parser.parse_args()
    model_name = args.model

    logger.info(f"Starting embedding service on {args.host}:{args.port}")
    logger.info(f"Model: {model_name}")

    uvicorn.run(
        app,
        host=args.host,
        port=args.port,
        log_level="info"
    )


if __name__ == "__main__":
    main()
