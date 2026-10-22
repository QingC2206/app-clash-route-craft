"""Build the simple ClashRouteCraft monogram icon; not needed to run/publish the app."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

assets = Path(__file__).resolve().parents[1] / "src/ClashRouteCraft.Desktop/Assets"
assets.mkdir(exist_ok=True)
image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((6, 6, 250, 250), radius=54, fill="#285CC8")
font = ImageFont.truetype("C:/Windows/Fonts/seguisb.ttf", 173)
box = draw.textbbox((0, 0), "C", font=font)
draw.text(((256 - (box[2]-box[0]))/2-box[0], (256 - (box[3]-box[1]))/2-box[1]-3), "C", font=font, fill="white")
image.save(assets / "clashroutecraft.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
