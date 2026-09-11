"""Small asymmetric UV/material fixture and starting point; no rendering.

resoloop blender run examples/blender/build_prop.py --arg=artifacts/blender/prop.blend
"""
from pathlib import Path
import sys
import bpy

output = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
output.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.context.scene.unit_settings.system = "METRIC"
bpy.context.scene.unit_settings.scale_length = 1

# A tiny asymmetric atlas makes flipped UVs obvious: red/green upper, blue/yellow lower.
size = 64
image = bpy.data.images.new("UV_orientation", width=size, height=size, alpha=False)
pixels = []
for y in range(size):
    for x in range(size):
        color = ((1, 0.05, 0.05, 1) if x < size // 2 else (0.05, 1, 0.05, 1)) if y >= size // 2 else (
            (0.05, 0.05, 1, 1) if x < size // 2 else (1, 1, 0.05, 1))
        pixels.extend(color)
image.pixels[:] = pixels
image.pack()
material = bpy.data.materials.new("Atlas")
material.use_nodes = True
shader = material.node_tree.nodes.get("Principled BSDF")
shader.inputs["Roughness"].default_value = 0.35
texture = material.node_tree.nodes.new("ShaderNodeTexImage")
texture.image = image
material.node_tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])

# Explicit front panel faces Blender -Y = Resonite -Z, with a full 0..1 UV rectangle.
mesh = bpy.data.meshes.new("Panel")
mesh.from_pydata([(-0.45, -0.26, -0.3), (0.45, -0.26, -0.3), (0.45, -0.26, 0.3), (-0.45, -0.26, 0.3)], [], [(0, 1, 2, 3)])
uv = mesh.uv_layers.new(name="UVMap")
for loop, value in zip(uv.data, [(0, 0), (1, 0), (1, 1), (0, 1)]):
    loop.uv = value
panel = bpy.data.objects.new("UV_Panel", mesh)
bpy.context.collection.objects.link(panel)
panel.data.materials.append(material)

metal = bpy.data.materials.new("Painted_metal")
metal.use_nodes = True
shader = metal.node_tree.nodes.get("Principled BSDF")
shader.inputs["Base Color"].default_value = (0.12, 0.22, 0.3, 1)
shader.inputs["Metallic"].default_value = 0.65
shader.inputs["Roughness"].default_value = 0.28
normal_image = bpy.data.images.new("Flat_normal", width=8, height=8)
normal_image.colorspace_settings.name = "Non-Color"
normal_image.pixels[:] = [0.5, 0.5, 1, 1] * 64
normal_image.pack()
normal_texture = metal.node_tree.nodes.new("ShaderNodeTexImage")
normal_texture.image = normal_image
normal_node = metal.node_tree.nodes.new("ShaderNodeNormalMap")
metal.node_tree.links.new(normal_texture.outputs["Color"], normal_node.inputs["Color"])
metal.node_tree.links.new(normal_node.outputs["Normal"], shader.inputs["Normal"])
bpy.ops.mesh.primitive_cube_add(size=1)
body = bpy.context.object
body.name = "Beveled_body"
body.scale = (1.1, 0.5, 0.8)
bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
body.data.materials.append(metal)
body.data.materials.append(material)
body.data.polygons[5].material_index = 1
bevel = body.modifiers.new("Silhouette_bevel", "BEVEL")
bevel.width = 0.055
bevel.segments = 3

bpy.ops.mesh.primitive_torus_add(major_segments=32, minor_segments=8, location=(0.28, 0, 0.52), major_radius=0.22, minor_radius=0.04)
handle = bpy.context.object
handle.name = "Handle"
handle.rotation_euler.x = 1.5707963267948966
handle.data.materials.append(metal)
for polygon in handle.data.polygons:
    polygon.use_smooth = True

bpy.ops.wm.save_as_mainfile(filepath=str(output))
