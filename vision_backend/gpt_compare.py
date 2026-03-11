import base64
import re
from openai import OpenAI

client = OpenAI()


def encode_image(path):

    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode()


def compare_two_images(img1_path, img2_path):

    img1 = encode_image(img1_path)
    img2 = encode_image(img2_path)

    prompt = """
You are comparing two reinforcement bar drawings.

Focus ONLY on the black bar polyline.

Completely ignore:
- text labels (L1, L2, L3, etc.)
- arrows
- dimensions
- colors
- line thickness
- annotations

STEP 1
Trace the bar in each image from one end to the other and determine:
• the number of straight segments
• the sequence of directions of those segments

Allowed directions:
UP, DOWN, LEFT, RIGHT

Example:
DOWN → RIGHT → UP

STEP 2
Compare the two sequences.

STRICT RULES

1. The number of segments must match exactly.
2. The direction order must match exactly.
3. The bend positions must match.
4. Rotation is NOT allowed.
5. Mirroring is NOT allowed.

Examples:

DOWN → RIGHT → UP
vs
DOWN → RIGHT → UP
→ identical shape

DOWN → RIGHT → UP
vs
UP → RIGHT → DOWN
→ mirrored → different

DOWN → RIGHT → UP
vs
RIGHT → DOWN → LEFT
→ rotated → different

DOWN → RIGHT
vs
DOWN → RIGHT → UP
→ different number of segments

SCORING

100 = identical geometry and identical segment sequence  
90–99 = identical geometry but minor drawing noise  
50–80 = same segment count but different orientation  
0–40 = different topology (extra or missing segments)

IMPORTANT

Score 100 ONLY when both:
• segment count is identical
• direction sequence is identical.

Return ONLY:

SCORE: number
"""

    response = client.responses.create(
        model="gpt-5.2",
        input=[{
            "role": "user",
            "content": [
                {"type": "input_text", "text": prompt},
                {"type": "input_image", "image_url": f"data:image/png;base64,{img1}"},
                {"type": "input_image", "image_url": f"data:image/png;base64,{img2}"}
            ]
        }]
    )

    text = response.output_text.strip()

    match = re.search(r'\d+', text)

    if match:
        score = int(match.group())
    else:
        score = 0

    return score