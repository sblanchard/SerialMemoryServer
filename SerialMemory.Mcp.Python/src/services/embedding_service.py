"""Embedding service using sentence-transformers."""

import logging
from typing import List

import torch
from sentence_transformers import SentenceTransformer

from ..config import settings

logger = logging.getLogger(__name__)


class EmbeddingService:
    """Generates semantic embeddings for text using sentence-transformers."""

    def __init__(self):
        self.model: SentenceTransformer | None = None
        self.device = "cuda" if torch.cuda.is_available() else "cpu"

    async def initialize(self):
        """Load the embedding model."""
        logger.info(f"Loading embedding model: {settings.embedding_model}")
        self.model = SentenceTransformer(
            settings.embedding_model,
            device=self.device,
        )
        logger.info(f"Embedding model loaded on {self.device}")

    def embed_text(self, text: str) -> List[float]:
        """Generate embedding for a single text."""
        if not self.model:
            raise RuntimeError("Embedding model not initialized")

        embedding = self.model.encode(
            text,
            convert_to_numpy=True,
            normalize_embeddings=True,
        )
        return embedding.tolist()

    def embed_batch(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings for multiple texts efficiently."""
        if not self.model:
            raise RuntimeError("Embedding model not initialized")

        embeddings = self.model.encode(
            texts,
            batch_size=settings.embedding_batch_size,
            convert_to_numpy=True,
            normalize_embeddings=True,
            show_progress_bar=False,
        )
        return embeddings.tolist()

    @property
    def embedding_dimension(self) -> int:
        """Get the dimension of the embeddings."""
        if not self.model:
            raise RuntimeError("Embedding model not initialized")
        return self.model.get_sentence_embedding_dimension()


# Global embedding service instance
embedding_service = EmbeddingService()
