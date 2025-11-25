"""Entity extraction service using spaCy NER."""

import logging
from dataclasses import dataclass
from typing import List

import spacy
from spacy.language import Language

from ..config import settings

logger = logging.getLogger(__name__)


@dataclass
class Entity:
    """Extracted entity with metadata."""

    text: str
    label: str  # Entity type (PERSON, ORG, GPE, DATE, etc.)
    start: int  # Character start position
    end: int  # Character end position
    confidence: float = 1.0


@dataclass
class Relationship:
    """Extracted relationship between entities."""

    source_entity: str
    target_entity: str
    relation_type: str
    confidence: float = 0.8


class EntityExtractionService:
    """Extracts entities and relationships from text using spaCy."""

    def __init__(self):
        self.nlp: Language | None = None

    async def initialize(self):
        """Load the spaCy model."""
        logger.info(f"Loading spaCy model: {settings.spacy_model}")

        try:
            self.nlp = spacy.load(settings.spacy_model)
        except OSError:
            logger.warning(
                f"Model {settings.spacy_model} not found. Downloading..."
            )
            # Download the model if not available
            import subprocess
            subprocess.run(
                ["python", "-m", "spacy", "download", settings.spacy_model],
                check=True,
            )
            self.nlp = spacy.load(settings.spacy_model)

        logger.info("spaCy model loaded")

    def extract_entities(self, text: str) -> List[Entity]:
        """Extract named entities from text."""
        if not self.nlp:
            raise RuntimeError("spaCy model not initialized")

        doc = self.nlp(text)
        entities = []

        for ent in doc.ents:
            entities.append(
                Entity(
                    text=ent.text,
                    label=ent.label_,
                    start=ent.start_char,
                    end=ent.end_char,
                )
            )

        return entities

    def extract_relationships(self, text: str) -> List[Relationship]:
        """Extract relationships between entities using dependency parsing."""
        if not self.nlp:
            raise RuntimeError("spaCy model not initialized")

        doc = self.nlp(text)
        relationships = []

        # Create entity map for quick lookup
        entity_map = {ent.start: ent for ent in doc.ents}

        # Extract subject-verb-object triples
        for token in doc:
            # Look for verbs
            if token.pos_ == "VERB":
                # Find subjects
                subjects = [child for child in token.children if child.dep_ in ("nsubj", "nsubjpass")]
                # Find objects
                objects = [child for child in token.children if child.dep_ in ("dobj", "pobj", "attr")]

                for subj in subjects:
                    for obj in objects:
                        # Check if subject and object are entities
                        subj_ent = self._get_entity_for_token(subj, entity_map)
                        obj_ent = self._get_entity_for_token(obj, entity_map)

                        if subj_ent and obj_ent:
                            # Use the verb as the relationship type
                            relation = token.lemma_.upper()
                            relationships.append(
                                Relationship(
                                    source_entity=subj_ent.text,
                                    target_entity=obj_ent.text,
                                    relation_type=relation,
                                    confidence=0.7,
                                )
                            )

        # Extract prepositional relationships
        relationships.extend(self._extract_prepositional_relationships(doc, entity_map))

        # Extract possessive relationships
        relationships.extend(self._extract_possessive_relationships(doc, entity_map))

        return relationships

    def _get_entity_for_token(self, token, entity_map):
        """Get the entity that contains this token."""
        for ent in entity_map.values():
            if ent.start <= token.i < ent.end:
                return ent
        return None

    def _extract_prepositional_relationships(self, doc, entity_map) -> List[Relationship]:
        """Extract relationships based on prepositional phrases."""
        relationships = []

        for token in doc:
            if token.dep_ == "prep":
                # Get the head (what the prep is modifying)
                head = token.head
                head_ent = self._get_entity_for_token(head, entity_map)

                # Get the object of the preposition
                for child in token.children:
                    if child.dep_ == "pobj":
                        obj_ent = self._get_entity_for_token(child, entity_map)

                        if head_ent and obj_ent:
                            # Use preposition as relationship
                            relation = token.text.upper()
                            relationships.append(
                                Relationship(
                                    source_entity=head_ent.text,
                                    target_entity=obj_ent.text,
                                    relation_type=relation,
                                    confidence=0.6,
                                )
                            )

        return relationships

    def _extract_possessive_relationships(self, doc, entity_map) -> List[Relationship]:
        """Extract possessive relationships (e.g., 'John's company')."""
        relationships = []

        for token in doc:
            if token.dep_ == "poss":
                possessor = token
                possessed = token.head

                poss_ent = self._get_entity_for_token(possessor, entity_map)
                possessed_ent = self._get_entity_for_token(possessed, entity_map)

                if poss_ent and possessed_ent:
                    relationships.append(
                        Relationship(
                            source_entity=poss_ent.text,
                            target_entity=possessed_ent.text,
                            relation_type="OWNS",
                            confidence=0.8,
                        )
                    )

        return relationships

    def extract_entities_and_relationships(
        self, text: str
    ) -> tuple[List[Entity], List[Relationship]]:
        """Extract both entities and relationships in one pass."""
        entities = self.extract_entities(text)
        relationships = self.extract_relationships(text)
        return entities, relationships


# Global entity extraction service instance
entity_service = EntityExtractionService()
