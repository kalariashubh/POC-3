import os
import shutil
import json

from config import (
    PREVIEW_FOLDER,
    LIBRARY_FOLDER,
    METADATA_FILE,
    ADDED_SHAPES_FOLDER,
    ADDED_METADATA_FILE
)

from utils import get_next_shape_id, get_image_hash
from gpt_compare import compare_two_images


def load_metadata(path):

    if not os.path.exists(path):
        return {}

    if os.path.getsize(path) == 0:
        return {}

    with open(path, "r") as f:
        return json.load(f)


def save_metadata(path, data):

    category_order = ["beam", "column", "footing", "slab"]

    def sort_key(key):

        key_lower = key.lower()

        for idx, cat in enumerate(category_order):
            if key_lower.startswith(cat):
                number = key_lower.replace(cat, "")
                try:
                    number = int(number)
                except:
                    number = 0
                return (idx, number)

        return (len(category_order), key)

    sorted_keys = sorted(data.keys(), key=sort_key)

    sorted_dict = {k: data[k] for k in sorted_keys}

    with open(path, "w") as f:
        json.dump(sorted_dict, f, indent=4)


def ensure_added_folder():

    if not os.path.exists(ADDED_SHAPES_FOLDER):
        os.makedirs(ADDED_SHAPES_FOLDER)


def get_latest_preview():

    if not os.path.exists(PREVIEW_FOLDER):
        return None

    previews = [
        os.path.join(PREVIEW_FOLDER, f)
        for f in os.listdir(PREVIEW_FOLDER)
        if f.lower().endswith(".png")
    ]

    if not previews:
        return None

    return max(previews, key=os.path.getctime)


def load_signature_from_preview(preview_png):

    json_file = preview_png.replace(".png", ".json")

    if not os.path.exists(json_file):
        return {}

    with open(json_file, "r") as f:
        data = json.load(f)

    return data.get("signature", {})


def get_shapes_without_signature(metadata, category):

    shapes = []

    for key, data in metadata.items():

        if not key.lower().startswith(category):
            continue

        signatures = data.get("signatures")

        if not signatures:
            shapes.append(key + ".png")

    return shapes


def signature_match(input_signature, metadata, category):

    matches = []

    if not input_signature:
        return matches

    input_dir = input_signature.get("segment_directions", [])
    input_rev = input_signature.get("reverse_directions", [])

    for shape_id, data in metadata.items():

        if not shape_id.lower().startswith(category):
            continue

        signatures = data.get("signatures")

        if not signatures:
            continue

        for sig in signatures:

            if (
                sig.get("topology") == input_signature.get("topology")
                and sig.get("segment_count") == input_signature.get("segment_count")
                and sig.get("signed_angles") == input_signature.get("signed_angles")
                and sig.get("first_last_parallel") == input_signature.get("first_last_parallel")
            ):

                lib_dir = sig.get("segment_directions", [])
                lib_rev = sig.get("reverse_directions", [])

                if (
                    input_dir == lib_dir
                    or input_dir == lib_rev
                    or input_rev == lib_dir
                    or input_rev == lib_rev
                ):
                    matches.append(shape_id + ".png")
                    break

    return matches


def main():

    metadata = load_metadata(METADATA_FILE)
    added_metadata = load_metadata(ADDED_METADATA_FILE)

    ensure_added_folder()

    latest = get_latest_preview()

    if not latest:
        print("No preview images found")
        return

    shape_name = os.path.basename(latest)

    print("\nChecking shape:", shape_name)

    valid_categories = ["beam", "column", "footing", "slab"]

    while True:

        category = input(
            "\nWhich category of shape are you checking? (beam/column/footing/slab): "
        ).lower()

        if category in valid_categories:
            break

        print("Invalid input. Please type: beam, column, footing, or slab.")

    print("\nSearching for similar shapes in the library...")

    category_files = [
        f for f in os.listdir(LIBRARY_FOLDER)
        if f.lower().startswith(category) and f.lower().endswith(".png")
    ]

    # IMAGE HASH
    test_hash = get_image_hash(latest)

    imagehash_matches = []

    for file in category_files:

        lib_path = os.path.join(LIBRARY_FOLDER, file)

        lib_hash = get_image_hash(lib_path)

        if test_hash == lib_hash:
            imagehash_matches.append(file)

    # SIGNATURE
    input_signature = load_signature_from_preview(latest)

    signature_matches = signature_match(input_signature, metadata, category)

    # VISION MODEL
    vision_matches = []

    shapes_without_signature = get_shapes_without_signature(metadata, category)

    if shapes_without_signature:

        for file in shapes_without_signature:

            lib_path = os.path.join(LIBRARY_FOLDER, file)

            if not os.path.exists(lib_path):
                continue

            score = compare_two_images(latest, lib_path)

            if score >= 95:
                vision_matches.append(file)

    # PRINT RESULTS

    if imagehash_matches:

        print("\nIMAGEHASH MATCHES")

        for m in imagehash_matches:

            key = m.replace(".png", "")

            info = metadata.get(key)

            print("\nMatched with:", m)

            if info:
                print("Category:", info["category"])
                print("Name:", info["name"])

    if signature_matches:

        print("\nSIGNATURE MATCHES")

        for m in signature_matches:

            key = m.replace(".png", "")

            info = metadata.get(key)

            print("\nMatched with:", m)

            if info:
                print("Category:", info["category"])
                print("Name:", info["name"])

    if vision_matches:

        print("\nVISION MODEL MATCHES")

        for m in vision_matches:

            key = m.replace(".png", "")

            info = metadata.get(key)

            print("\nMatched with:", m)

            if info:
                print("Category:", info["category"])
                print("Name:", info["name"])

    if not imagehash_matches and not signature_matches and not vision_matches:

        print("\nNO MATCH FOUND")

    vision_match_found = (
        len(vision_matches) > 0 and
        len(imagehash_matches) == 0 and
        len(signature_matches) == 0
    )

    # ASK USER

    while True:

        choice = input("\nDo you wish to add shape to the library? (yes/no): ").lower()

        if choice in ["yes", "no"]:
            break

        print("Invalid input. Please type yes or no.")

    if choice == "no":

        print("\nNo new shape was added to the library")
        return

    name = input("\nEnter shape name: ")

    new_id = get_next_shape_id(LIBRARY_FOLDER, category)

    new_filename = f"{category}{new_id}.png"

    library_path = os.path.join(LIBRARY_FOLDER, new_filename)
    added_path = os.path.join(ADDED_SHAPES_FOLDER, new_filename)

    shutil.copy(latest, library_path)
    shutil.copy(latest, added_path)

    key = new_filename.replace(".png", "")

    # LOAD SIGNATURE
    signature = load_signature_from_preview(latest)

    if vision_match_found:

        metadata[key] = {
            "category": category.capitalize(),
            "name": name
        }

        added_metadata[key] = {
            "category": category.capitalize(),
            "name": name
        }

    else:

        metadata[key] = {
            "category": category.capitalize(),
            "name": name,
            "signatures": [signature]
        }

        added_metadata[key] = {
            "category": category.capitalize(),
            "name": name,
            "signatures": [signature]
        }

    save_metadata(METADATA_FILE, metadata)

    with open(ADDED_METADATA_FILE, "w") as f:
        json.dump(added_metadata, f, indent=4)

    print("\nNEW SHAPE ADDED")
    print("Filename:", new_filename)
    print("Category:", category.capitalize())
    print("Shape Name:", name)


if __name__ == "__main__":
    main()