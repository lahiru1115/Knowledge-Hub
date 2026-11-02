"""Validate an OpenAPI document and check that every operationId is unique."""
import sys
from collections import Counter

import yaml
from openapi_spec_validator import validate

HTTP_METHODS = {"get", "put", "post", "delete", "patch", "options", "head", "trace"}

doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
validate(doc)

ids = [op["operationId"] for item in doc["paths"].values()
       for method, op in item.items() if method in HTTP_METHODS]
dupes = [i for i, n in Counter(ids).items() if n > 1]
if dupes:
    sys.exit(f"Duplicate operationId: {dupes}")
print(f"OK: {len(doc['paths'])} paths, {len(ids)} operations")
