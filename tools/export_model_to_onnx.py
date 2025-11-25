"""
Export sentence-transformers model to ONNX format for use in C# ONNX Runtime

Usage:
    python export_model_to_onnx.py
"""

from sentence_transformers import SentenceTransformer
from pathlib import Path
import torch

# Model to export
MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2"
OUTPUT_DIR = Path("./onnx_models/all-MiniLM-L6-v2")

def export_to_onnx():
    print(f"Loading model: {MODEL_NAME}")
    model = SentenceTransformer(MODEL_NAME)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    # Get the transformer model (BERT)
    transformer = model[0].auto_model
    tokenizer = model.tokenizer

    # Save tokenizer
    tokenizer.save_pretrained(str(OUTPUT_DIR))
    print(f"Saved tokenizer to {OUTPUT_DIR}")

    # Create dummy input
    dummy_input = {
        "input_ids": torch.randint(0, 30522, (1, 128)),  # vocab_size=30522 for BERT
        "attention_mask": torch.ones((1, 128), dtype=torch.long),
    }

    # Export to ONNX
    onnx_path = OUTPUT_DIR / "model.onnx"
    torch.onnx.export(
        transformer,
        (dummy_input["input_ids"], dummy_input["attention_mask"]),
        str(onnx_path),
        input_names=["input_ids", "attention_mask"],
        output_names=["last_hidden_state"],
        dynamic_axes={
            "input_ids": {0: "batch", 1: "sequence"},
            "attention_mask": {0: "batch", 1: "sequence"},
            "last_hidden_state": {0: "batch", 1: "sequence"},
        },
        opset_version=14,
    )

    print(f"✓ Exported ONNX model to {onnx_path}")
    print(f"\nModel info:")
    print(f"  - Embedding dimension: {model.get_sentence_embedding_dimension()}")
    print(f"  - Max sequence length: {model.max_seq_length}")
    print(f"\nTo use in C#:")
    print(f"  var embedder = new OnnxEmbeddingService(")
    print(f'      "{onnx_path.absolute()}",')
    print(f'      "{(OUTPUT_DIR / "tokenizer.json").absolute()}"')
    print(f"  );")

if __name__ == "__main__":
    try:
        export_to_onnx()
    except Exception as e:
        print(f"Error: {e}")
        print("\nMake sure you have installed:")
        print("  pip install sentence-transformers torch onnx")
