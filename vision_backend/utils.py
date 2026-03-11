import os
import hashlib


def get_next_shape_id(library_folder, category):

    max_id = 0

    for file in os.listdir(library_folder):

        if file.lower().startswith(category.lower()) and file.lower().endswith(".png"):

            number = file.lower().replace(category.lower(), "").replace(".png", "")

            try:
                number = int(number)
                max_id = max(max_id, number)
            except:
                pass

    return max_id + 1


def get_image_hash(path):

    with open(path, "rb") as f:
        data = f.read()

    return hashlib.md5(data).hexdigest()