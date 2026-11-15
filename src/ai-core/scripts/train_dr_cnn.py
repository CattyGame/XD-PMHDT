"""Frozen ImageNet ResNet34 transfer learning; select on validation, evaluate test once."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps
import torch
from torchvision.models import ResNet34_Weights, resnet34

ROOT = Path(__file__).resolve().parents[3]


def auc(labels, scores):
    positive = np.asarray(scores)[np.asarray(labels) == 1]
    negative = np.asarray(scores)[np.asarray(labels) == 0]
    if not len(positive) or not len(negative):
        raise ValueError("AUC requires both classes.")
    return float(((positive[:, None] > negative).sum() + 0.5 * (positive[:, None] == negative).sum()) / (len(positive) * len(negative)))


def train(output: Path, epochs: int = 100):
    if epochs < 1:
        raise ValueError("epochs must be positive")
    torch.manual_seed(42)
    torch.set_num_threads(2)
    manifest = ROOT / "docs/datasets/hrf/manifest.csv"
    rows = list(csv.DictReader(manifest.open(encoding="utf-8-sig")))
    if len({r["patient_id"] for r in rows}) != len(rows):
        raise ValueError("Patient overlap in the dataset splits.")
    if len({r["image_sha256"] for r in rows}) != len(rows):
        raise ValueError("Duplicate image content in the dataset splits.")
    if {category: sum(r["category"] == category for r in rows) for category in ("dr", "h", "g")} != {"dr": 15, "h": 15, "g": 15} or len(rows) != 45:
        raise ValueError("Expected the documented HRF cohort: 15 DR, 15 healthy, 15 glaucoma.")
    if any(r["split"] not in ("train", "validation", "test") for r in rows):
        raise ValueError("Unknown dataset split.")
    model = resnet34(weights=ResNet34_Weights.IMAGENET1K_V1)
    for parameter in model.parameters():
        parameter.requires_grad_(False)
    features = model.fc.in_features
    model.fc = torch.nn.Identity()
    model.eval()
    transform = ResNet34_Weights.IMAGENET1K_V1.transforms()
    batches = {split: ([], [], []) for split in ("train", "validation", "test", "glaucoma_ood")}
    for row in rows:
        path = ROOT / row["image_path"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != row["image_sha256"]:
            raise ValueError(f"Image hash mismatch: {path}")
        split = "glaucoma_ood" if row["category"] == "g" else row["split"]
        with Image.open(path) as source:
            image = source.convert("RGB")
        images = [image, ImageOps.mirror(image)] if split == "train" else [image]
        with torch.no_grad():
            vectors = model(torch.stack([transform(im) for im in images]))
        batches[split][0].extend(vectors)
        batches[split][1].extend([int(row["category"] == "dr")] * len(images))
        batches[split][2].extend([row["patient_id"]] * len(images))
    tensors = {k: (torch.stack(v[0]), torch.tensor(v[1], dtype=torch.float32).view(-1, 1)) for k, v in batches.items()}
    head = torch.nn.Linear(features, 1)
    optimizer = torch.optim.Adam(head.parameters(), lr=0.005, weight_decay=0.01)
    loss_fn = torch.nn.BCEWithLogitsLoss()
    best_metric, best_state, best_epoch = (-1.0, float("-inf")), None, None
    for epoch in range(epochs):
        optimizer.zero_grad()
        loss_fn(head(tensors["train"][0]), tensors["train"][1]).backward()
        optimizer.step()
        with torch.no_grad():
            validation_logits = head(tensors["validation"][0])
            validation_loss = float(loss_fn(validation_logits, tensors["validation"][1]))
            validation_auc = auc(batches["validation"][1], validation_logits.view(-1).tolist())
        metric = (validation_auc, -validation_loss)
        if metric > best_metric:
            best_metric, best_epoch = metric, epoch + 1
            best_state = {k: v.detach().clone() for k, v in head.state_dict().items()}
    head.load_state_dict(best_state)
    model.fc = head
    report = {"architecture": "resnet34", "task": "hrf_dr_vs_healthy", "seed": 42,
              "pretrained_weights": "IMAGENET1K_V1", "frozen_backbone": True,
              "manifest_sha256": hashlib.sha256(manifest.read_bytes()).hexdigest(),
              "selected_epoch": best_epoch, "selection": "maximum validation AUC, tie-break minimum BCE; test not used for selection",
              "torch": str(torch.__version__), "research_only": True, "splits": {}}
    for split in batches:
        with torch.no_grad():
            scores = torch.sigmoid(head(tensors[split][0])).view(-1).tolist()
        labels = batches[split][1]
        report["splits"][split] = {"patient_ids": batches[split][2], "labels": labels if split != "glaucoma_ood" else None,
                                   "probabilities": scores, "auc": auc(labels, scores) if split != "glaucoma_ood" else None}
    report["validation_auc_target"] = 0.88
    report["validation_target_met"] = report["splits"]["validation"]["auc"] >= 0.88
    output.parent.mkdir(parents=True, exist_ok=True)
    torch.save({"state_dict": model.state_dict(), "metadata": report}, output)
    report["checkpoint_sha256"] = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"checkpoint": str(output), "validation_target_met": report["validation_target_met"],
                      "validation_auc": report["splits"]["validation"]["auc"], "test_auc": report["splits"]["test"]["auc"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--epochs", type=int, default=100)
    args = parser.parse_args()
    train(args.output, args.epochs)
