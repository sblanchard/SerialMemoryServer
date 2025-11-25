"""
Simple HTTP service for embeddings that C# can call

Usage:
    pip install fastapi uvicorn sentence-transformers
    python embedding_http_service.py

Then from C#: use HttpEmbeddingService("http://localhost:8765")
"""

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from sentence_transformers import SentenceTransformer
from typing import List
import uvicorn

app = FastAPI(title="Embedding Service", version="1.0")

# Allow CORS for local development
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# Load model at startup
print("Loading sentence-transformers model...")
model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')
print(f"Model loaded! Embedding dimension: {model.get_sentence_embedding_dimension()}")

class EmbedRequest(BaseModel):
    text: str

class EmbedBatchRequest(BaseModel):
    texts: List[str]

@app.post("/embed")
def embed(request: EmbedRequest):
    """Generate embedding for a single text"""
    try:
        embedding = model.encode(request.text, normalize_embeddings=True)
        return {"embedding": embedding.tolist()}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/embed-batch")
def embed_batch(request: EmbedBatchRequest):
    """Generate embeddings for multiple texts"""
    try:
        embeddings = model.encode(
            request.texts,
            normalize_embeddings=True,
            show_progress_bar=False
        )
        return {"embeddings": [emb.tolist() for emb in embeddings]}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/health")
def health():
    """Health check endpoint"""
    return {
        "status": "healthy",
        "model": model.get_sentence_embedding_dimension(),
        "dimension": model.get_sentence_embedding_dimension()
    }

@app.get("/")
def root():
    """Root endpoint with usage info"""
    return {
        "service": "Embedding Service",
        "model": "sentence-transformers/all-MiniLM-L6-v2",
        "dimension": model.get_sentence_embedding_dimension(),
        "endpoints": {
            "POST /embed": "Generate single embedding",
            "POST /embed-batch": "Generate multiple embeddings",
            "GET /health": "Health check"
        }
    }

if __name__ == "__main__":
    print("\n" + "="*50)
    print("🚀 Embedding HTTP Service")
    print("="*50)
    print(f"Model: sentence-transformers/all-MiniLM-L6-v2")
    print(f"Dimension: {model.get_sentence_embedding_dimension()}")
    print(f"URL: http://localhost:8765")
    print("="*50 + "\n")

    uvicorn.run(app, host="0.0.0.0", port=8765, log_level="info")
