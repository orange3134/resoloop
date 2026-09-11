"""Static Blender -> ResoniteLink 0.13.1 bundle. Executed by resoloop blender export.

Schema: Yellow-Dog-Man/ResoniteLink commit 067afad3d1977b3806cbad077f4e8a534f56f451.
No rendering, implicit baking, simplification, or world mutation.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys

import bpy
from mathutils import Matrix


def key(prefix, name):
    slug = re.sub(r"[^a-zA-Z0-9_-]", "_", name)[:40]
    return prefix + slug + "_" + hashlib.sha256(name.encode()).hexdigest()[:8]


def vector(value, axes="xyz"):
    return dict(zip(axes, (float(x) for x in value)))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")) + "\n", encoding="utf-8")


def socket_value(socket):
    value = socket.default_value
    return tuple(value) if hasattr(value, "__len__") else value


def srgb_color(linear):
    # Blender socket colors are linear; the CLI's colorX tuple syntax uses runtime sRGB.
    return [12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055 for c in linear[:3]] + [linear[3]]


def triangulated_copy(source):
    """Tessellation alone leaves n-gons that Blender's tangent calculator rejects.

Map every new triangle corner back to its source loop, including custom normals.
Only create a detached mesh when needed; never edit the authored/evaluated source.
"""
    if not any(p.loop_total > 4 for p in source.polygons):
        return None
    result = bpy.data.meshes.new("ResoLoop_Triangulated")
    try:
        triangles = list(source.loop_triangles)
        loops = [i for triangle in triangles for i in triangle.loops]
        result.from_pydata([v.co for v in source.vertices], [], [tuple(t.vertices) for t in triangles])
        for material in source.materials:
            result.materials.append(material)
        for polygon, triangle in zip(result.polygons, triangles):
            original = source.polygons[triangle.polygon_index]
            polygon.material_index = original.material_index
            polygon.use_smooth = original.use_smooth
        for layer in source.uv_layers:
            new_layer = result.uv_layers.new(name=layer.name)
            new_layer.data.foreach_set("uv", [v for i in loops for v in layer.data[i].uv])
            new_layer.active_render = layer.active_render
        if source.uv_layers.active:
            result.uv_layers.active_index = source.uv_layers.active_index
        color = source.color_attributes.active_color
        if color:
            new_color = result.color_attributes.new(name=color.name, type=color.data_type, domain=color.domain)
            indices = loops if color.domain == "CORNER" else range(len(source.vertices))
            new_color.data.foreach_set("color", [v for i in indices for v in color.data[i].color])
            result.color_attributes.active_color = new_color
        result.normals_split_custom_set([source.corner_normals[i].vector for i in loops])
        result.calc_loop_triangles()
        return result
    except Exception:
        bpy.data.meshes.remove(result)
        raise


class Exporter:
    def __init__(self, args):
        self.args = args
        self.output = Path(args.output).resolve()
        self.assets = {}
        self.components = []
        self.materials = {}
        self.textures = {}
        self.meshes = []
        self.warnings = []
        temporary = bpy.data.materials.new("ResoLoop_Exporter_Defaults")
        try:
            temporary.use_nodes = True
            principled = temporary.node_tree.nodes.get("Principled BSDF")
            self.defaults = {s.name: socket_value(s) for s in principled.inputs if hasattr(s, "default_value")}
        finally:
            bpy.data.materials.remove(temporary)

    def texture(self, node, normal=False):
        if node.type != "TEX_IMAGE" or node.image is None:
            raise ValueError("Bake the linked shader input to an Image Texture first.")
        supported_image = node.image.type == "IMAGE" or (node.image.source == "GENERATED" and node.image.type == "UV_TEST")
        if node.image.source not in {"FILE", "GENERATED"} or not supported_image:
            raise ValueError("Use a single still image; UDIM, movie and sequence textures are unsupported.")
        if node.projection != "FLAT" or node.extension != "REPEAT" or node.interpolation != "Linear":
            raise ValueError("Use Flat/Repeat/Linear Image Texture sampling for this exporter.")
        if node.inputs["Vector"].is_linked:
            raise ValueError("Use the active UV map with an unlinked Image Texture Vector; bake mapping transforms first.")
        image = node.image
        if normal and image.colorspace_settings.name != "Non-Color":
            raise ValueError("Normal map images must use Non-Color data.")
        if not normal and image.colorspace_settings.name != "sRGB":
            raise ValueError("Albedo/emission images must use sRGB; convert color-space explicitly first.")
        texture_key = key("tex_", image.name + ("_normal" if normal else "_color"))
        if texture_key in self.textures:
            return "$component:" + texture_key
        file_name = texture_key + ".png"
        width, height = image.size  # Request lazy loading before examining has_data.
        saved_path = bpy.path.abspath(image.filepath, library=image.library) if image.filepath else ""
        recovered = None
        try:
            if not image.has_data or width <= 0 or height <= 0:
                if saved_path and Path(saved_path).is_file():
                    recovered = bpy.data.images.load(saved_path, check_existing=False)
                    recovered.colorspace_settings.name = image.colorspace_settings.name
                    width, height = recovered.size
                    self.warnings.append(f"Recovered texture '{image.name}' from saved file '{saved_path}'; verify it matches the authored image.")
                if recovered is None or not recovered.has_data or width <= 0 or height <= 0:
                    raise ValueError("No readable pixel buffer or saved image file.")
            source = recovered or image
            previous_format = source.file_format
            try:
                source.file_format = "PNG"
                # ID.copy() does not copy edited pixel buffers. Save the current buffer directly.
                source.save(filepath=str(self.output / file_name), save_copy=True)
            finally:
                source.file_format = previous_format
        except (RuntimeError, ValueError, OSError) as error:
            raise ValueError(f"Texture '{image.name}' (source={image.source}, path='{saved_path or '<unset>'}'): {error} "
                             "Save the authored pixels, reload the saved image and pack it before saving the .blend; "
                             "reopen the .blend and verify the image. Missing unsaved pixels cannot be recovered.") from error
        finally:
            if recovered:
                bpy.data.images.remove(recovered)
        self.assets[texture_key] = {"kind": "texture", "source": file_name}
        self.components.append({"key": texture_key, "type": "FrooxEngine.StaticTexture2D",
                                "fields": {"URL": "$asset:" + texture_key, "IsNormalMap": normal, "MipMaps": True}})
        self.textures[texture_key] = {"name": image.name, "width": width, "height": height,
                                     "normalMap": normal, "file": file_name,
                                     "rgba8MipBytesEstimate": math.ceil(width * height * 4 * 4 / 3)}
        return "$component:" + texture_key

    def material(self, material):
        material_key = key("mat_", "material:" + material.name) if material else "mat_default_unassigned"
        if material_key in self.materials:
            return "$component:" + material_key
        fields = {"AlbedoColor": [0.8, 0.8, 0.8, 1], "Metallic": 0, "Smoothness": 0.5}
        if material:
            if not material.use_nodes or material.node_tree is None:
                raise ValueError(material.name + ": use a Principled BSDF material.")
            outputs = [n for n in material.node_tree.nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output]
            if len(outputs) != 1 or not outputs[0].inputs["Surface"].is_linked:
                raise ValueError(material.name + ": one active Surface output is required.")
            output = outputs[0]
            if output.inputs["Volume"].is_linked or output.inputs["Displacement"].is_linked:
                raise ValueError(material.name + ": bake volume/displacement to supported static geometry/textures.")
            shader = output.inputs["Surface"].links[0].from_node
            if shader.type != "BSDF_PRINCIPLED":
                raise ValueError(material.name + ": Surface must connect directly to Principled BSDF.")
            allowed = {"Base Color", "Normal", "Emission Color"}
            if getattr(self.args, "pack_pbr", False):
                allowed |= {"Metallic", "Roughness"}
            mapped = allowed | {"Metallic", "Roughness", "Emission Strength", "Alpha"}
            for socket in shader.inputs:
                if socket.is_linked and socket.name not in allowed:
                    raise ValueError(material.name + ": unsupported linked input " + socket.name + "; bake/repack and bind explicitly in apply.")
                if socket.name not in mapped and socket.name in self.defaults and socket_value(socket) != self.defaults[socket.name]:
                    raise ValueError(material.name + ": unsupported non-default input " + socket.name)
            for name in ("Transmission Weight", "Coat Weight", "Subsurface Weight", "Sheen Weight", "Anisotropic IOR Level"):
                if name in shader.inputs and abs(shader.inputs[name].default_value) > 1e-6:
                    raise ValueError(material.name + ": unsupported " + name)
            if shader.inputs["Alpha"].default_value != 1:
                raise ValueError(material.name + ": transparency requires a reviewed Resonite material; export an opaque base first.")
            color = shader.inputs["Base Color"]
            if color.is_linked:
                link = color.links[0]
                if link.from_socket.name != "Color":
                    raise ValueError("Base Color must use the image Color output.")
                fields["AlbedoColor"] = [1, 1, 1, 1]
                fields["AlbedoTexture"] = self.texture(link.from_node)
            else:
                fields["AlbedoColor"] = list(color.default_value)
            fields["Metallic"] = float(shader.inputs["Metallic"].default_value)
            fields["Smoothness"] = 1 - float(shader.inputs["Roughness"].default_value)
            if any(shader.inputs[name].is_linked for name in ("Metallic", "Roughness")):
                fields["MetallicMap"] = self.packed_pbr(material_key, shader)
                fields["Metallic"] = fields["Smoothness"] = 1.0
            normal = shader.inputs["Normal"]
            if normal.is_linked:
                node = normal.links[0].from_node
                if node.type != "NORMAL_MAP" or node.space != "TANGENT" or node.uv_map or not node.inputs["Color"].is_linked or node.inputs["Strength"].is_linked:
                    raise ValueError("Use an image through a tangent-space Normal Map node on the active UV, with constant strength.")
                link = node.inputs["Color"].links[0]
                if link.from_socket.name != "Color":
                    raise ValueError("Normal Map must use the image Color output.")
                fields["NormalMap"] = self.texture(link.from_node, normal=True)
                fields["NormalScale"] = float(node.inputs["Strength"].default_value)
            emission = shader.inputs["Emission Color"]
            strength = float(shader.inputs["Emission Strength"].default_value)
            if emission.is_linked:
                link = emission.links[0]
                if link.from_socket.name != "Color":
                    raise ValueError("Emission must use the image Color output.")
                fields["EmissiveMap"] = self.texture(link.from_node)
                fields["EmissiveColor"] = [strength, strength, strength, 1]
            else:
                fields["EmissiveColor"] = [float(v) * strength for v in emission.default_value[:3]] + [1]
        fields["AlbedoColor"] = srgb_color(fields["AlbedoColor"])
        if "EmissiveColor" in fields:
            fields["EmissiveColor"] = srgb_color(fields["EmissiveColor"])
        self.components.append({"key": material_key, "type": "FrooxEngine.PBS_Metallic", "fields": fields})
        self.materials[material_key] = {"name": material.name if material else "Default", "fields": fields}
        return "$component:" + material_key

    def packed_pbr(self, material_key, shader):
        sources, dimensions = {}, set()
        for name in ("Metallic", "Roughness"):
            socket = shader.inputs[name]
            if not socket.is_linked:
                sources[name] = float(socket.default_value)
                continue
            link = socket.links[0]
            node = link.from_node
            if (node.type != "TEX_IMAGE" or not node.image or link.from_socket.name not in {"Color", "Alpha"}
                    or node.image.source not in {"FILE", "GENERATED"}
                    or node.image.colorspace_settings.name != "Non-Color"
                    or node.inputs["Vector"].is_linked or node.projection != "FLAT"
                    or node.extension != "REPEAT" or node.interpolation != "Linear"):
                raise ValueError(name + ": packing requires a direct Non-Color still Image Texture, active UV, Flat/Repeat/Linear sampling.")
            size = tuple(node.image.size)
            if not node.image.has_data or min(size) <= 0:
                raise ValueError(name + ": missing image pixels; save/reload/pack the source image.")
            dimensions.add(size)
            pixels = list(node.image.pixels)
            if link.from_socket.name == "Alpha":
                sources[name] = pixels[3::4]
            else:
                if any(max(pixels[i:i+3]) - min(pixels[i:i+3]) > 1e-5 for i in range(0, len(pixels), 4)):
                    raise ValueError(name + ": Color-to-scalar packing requires a grayscale image; bake the intended scalar first.")
                sources[name] = pixels[0::4]
        if len(dimensions) != 1:
            raise ValueError("PBR packing requires equal image dimensions; resample intentionally before export.")
        width, height = next(iter(dimensions))
        values = []
        for i in range(width * height):
            metallic, roughness = (sources[name][i] if isinstance(sources[name], list) else sources[name]
                                   for name in ("Metallic", "Roughness"))
            if not all(math.isfinite(v) and 0 <= v <= 1 for v in (metallic, roughness)):
                raise ValueError("PBR values must be finite and within 0..1.")
            values.extend((metallic, 1, 0, 1-roughness))
        texture_key = key("pbr_", material_key)
        filename = texture_key + ".png"
        packed = bpy.data.images.new(texture_key, width=width, height=height, alpha=True)
        try:
            packed.colorspace_settings.name = "Non-Color"
            packed.pixels[:] = values
            packed.file_format = "PNG"
            packed.save(filepath=str(self.output / filename), save_copy=True)
        finally:
            bpy.data.images.remove(packed)
        self.assets[texture_key] = {"kind": "texture", "source": filename}
        self.components.append({"key": texture_key, "type": "FrooxEngine.StaticTexture2D", "fields": {
            "URL": "$asset:" + texture_key, "PreferredProfile": "Linear", "MipMaps": True}})
        self.textures[texture_key] = {"name": texture_key, "file": filename, "width": width, "height": height,
            "normalMap": False, "dataMap": True, "channels": "R=metallic,G=1,B=0,A=smoothness", "profile": "Linear",
            "rgba8MipBytesEstimate": math.ceil(width * height * 4 * 4 / 3)}
        return "$component:" + texture_key

    def mesh(self, obj, depsgraph, units):
        if obj.data.shape_keys or any(m.type == "ARMATURE" for m in obj.modifiers):
            raise ValueError(obj.name + ": rigged meshes/shape keys are unsupported; explicitly make a static copy if intended.")
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
        triangulated = None
        try:
            mesh.calc_loop_triangles()
            triangulated = triangulated_copy(mesh)
            if triangulated is not None:
                mesh = triangulated
            if not mesh.loop_triangles:
                raise ValueError(obj.name + ": no triangles.")
            # Bake object/world transforms including nonuniform and mirrored scale, then Z-up -> Y-up.
            basis = Matrix(((1, 0, 0), (0, 0, 1), (0, 1, 0)))
            transform = Matrix.Identity(4) if getattr(self.args, "preserve_hierarchy", False) else obj.matrix_world
            linear = basis @ transform.to_3x3()
            if abs(linear.determinant()) < 1e-12:
                raise ValueError(obj.name + ": singular transform.")
            normal_matrix = linear.inverted().transposed()
            # Keep the triangle cross product aligned with the exported outward normal.
            # Axis reflection reverses winding; a mirrored object cancels that reversal.
            reverse_winding = (basis @ obj.matrix_world.to_3x3()).determinant() < 0
            uv_layers = list(mesh.uv_layers)
            active = next((layer for layer in uv_layers if layer.active_render), mesh.uv_layers.active)
            if active:
                uv_layers = [active] + [layer for layer in uv_layers if layer != active]
                mesh.calc_tangents(uvmap=active.name)
            color = mesh.color_attributes.active_color
            if color and color.domain not in {"POINT", "CORNER"}:
                raise ValueError("Unsupported color attribute domain: " + color.domain)
            groups = {}
            vertices, lookup = [], {}
            for triangle in mesh.loop_triangles:
                material = mesh.materials[triangle.material_index] if triangle.material_index < len(mesh.materials) else None
                reference = self.material(material)
                fields = self.materials[reference.removeprefix("$component:")]["fields"]
                if not active and any(field in fields for field in ("AlbedoTexture", "NormalMap", "EmissiveMap", "MetallicMap")):
                    raise ValueError(obj.name + ": textured material requires an unwrapped UV map.")
                indices = []
                for loop_index in triangle.loops:
                    loop = mesh.loops[loop_index]
                    position = basis @ (transform @ mesh.vertices[loop.vertex_index].co) * units
                    normal = (normal_matrix @ mesh.corner_normals[loop_index].vector).normalized()
                    vertex = {"position": vector(position), "normal": vector(normal)}
                    if active:
                        tangent = linear @ loop.tangent
                        tangent = (tangent - normal * tangent.dot(normal)).normalized()
                        sign = float(loop.bitangent_sign) * (-1 if linear.determinant() < 0 else 1)
                        vertex["tangent"] = vector((*tangent, sign), "xyzw")
                        vertex["uvs"] = [{"$type": "2D", "uv": vector(layer.data[loop_index].uv, "xy")} for layer in uv_layers]
                    if color:
                        item = color.data[loop_index if color.domain == "CORNER" else loop.vertex_index]
                        vertex["color"] = vector(item.color, "rgba")
                    identity = json.dumps(vertex, sort_keys=True, allow_nan=False)
                    if identity not in lookup:
                        lookup[identity] = len(vertices)
                        vertices.append(vertex)
                    indices.append(lookup[identity])
                if reverse_winding:
                    indices[1], indices[2] = indices[2], indices[1]
                groups.setdefault(reference, []).extend(indices)
            mesh_key = key("mesh_", obj.name)
            filename = mesh_key + ".mesh.json"
            write_json(self.output / filename, {"vertices": vertices, "submeshes": [
                {"$type": "trianglesFlat", "vertexIndices": indices} for indices in groups.values()]})
            self.assets[mesh_key] = {"kind": "mesh", "source": filename}
            self.meshes.append({"name": obj.name, "file": filename, "vertices": len(vertices),
                                "triangles": len(mesh.loop_triangles), "submeshes": len(groups),
                                "uvChannels": [layer.name for layer in uv_layers], "vertexColors": bool(color)})
            if color:
                self.warnings.append(obj.name + ": vertex colors are preserved in the mesh; PBS_Metallic does not automatically display them. Bind a verified vertex-color material if needed.")
            return {"slot": {"key": key("object_", obj.name), "name": obj.name}, "components": [
                {"key": mesh_key, "type": "FrooxEngine.StaticMesh", "fields": {"URL": "$asset:" + mesh_key}},
                {"key": key("renderer_", obj.name), "type": "FrooxEngine.MeshRenderer", "fields": {
                    "Mesh": "$component:" + mesh_key, "Materials": list(groups)}}]}
        finally:
            if triangulated is not None:
                bpy.data.meshes.remove(triangulated)
            evaluated.to_mesh_clear()

    def run(self):
        if not self.args.name.strip() or "/" in self.args.name:
            raise ValueError("Model name must be nonempty and contain no slash.")
        if self.output.exists():
            raise ValueError("Output directory already exists; choose a fresh bundle path.")
        collection = bpy.data.collections.get(self.args.collection) if self.args.collection else None
        if self.args.collection and collection is None:
            raise ValueError("Collection not found: " + self.args.collection)
        source = collection.all_objects if collection else bpy.context.scene.objects
        objects = sorted((o for o in source if o.visible_get() and not o.hide_render), key=lambda o: o.name)
        for obj in objects:
            if obj.type not in {"MESH", "EMPTY", "LIGHT", "CAMERA"}:
                raise ValueError(obj.name + ": convert " + obj.type + " to a mesh explicitly.")
            if obj.instance_type != "NONE" or any(m.type == "NODES" for m in obj.modifiers):
                raise ValueError(obj.name + ": realize instances and convert Geometry Nodes to an explicit mesh copy first.")
            if obj.animation_data or obj.constraints:
                raise ValueError(obj.name + ": animated/constrained transforms need an explicit static copy.")
        meshes = [o for o in objects if o.type == "MESH"]
        if not meshes:
            raise ValueError("No visible mesh objects in the selected scene/collection.")
        units = bpy.context.scene.unit_settings.scale_length
        if not math.isfinite(units) or units <= 0:
            raise ValueError("Scene unit scale must be positive and finite.")
        self.output.mkdir(parents=True)
        bpy.context.view_layer.update()
        depsgraph = bpy.context.evaluated_depsgraph_get()
        children = [self.mesh(obj, depsgraph, units) for obj in meshes]
        if getattr(self.args, "preserve_hierarchy", False):
            nodes = dict(zip(meshes, children))
            for obj in meshes:
                ancestor = obj.parent
                while ancestor:
                    nodes.setdefault(ancestor, {"slot": {"key": key("object_", ancestor.name), "name": ancestor.name}})
                    ancestor = ancestor.parent
            basis = Matrix(((1,0,0,0), (0,0,1,0), (0,1,0,0), (0,0,0,1)))
            children = []
            for obj, node in sorted(nodes.items(), key=lambda item: item[0].name):
                if obj.constraints or obj.animation_data:
                    raise ValueError(obj.name + ": animated/constrained ancestors require an explicit static copy.")
                local = obj.parent.matrix_world.inverted() @ obj.matrix_world if obj.parent else obj.matrix_world
                converted = basis @ local @ basis
                location, rotation, scale = converted.decompose()
                rebuilt = Matrix.LocRotScale(location, rotation, scale)
                if min(abs(v) for v in scale) < 1e-8 or max(abs(converted[r][c]-rebuilt[r][c]) for r in range(4) for c in range(4)) > 1e-5:
                    raise ValueError(obj.name + ": hierarchy transform has shear or singular scale; bake it explicitly.")
                node["slot"].update(position=list(location * units), rotation=[rotation.x, rotation.y, rotation.z, rotation.w], scale=list(scale))
                if obj.parent:
                    nodes[obj.parent].setdefault("children", []).append(node)
                else:
                    children.append(node)
        root_components = self.components
        if not getattr(self.args, "legacy_root_providers", False):
            # A provider's named Slot is identifiable before reference wiring completes, including after reconnect.
            children.append({"slot": {"key": "blender_providers", "name": "Blender_Providers"}, "children": [
                {"slot": {"key": "provider_" + c["key"], "name": c["key"]}, "components": [c]}
                for c in self.components]})
            root_components = []
        triangle_count = sum(m["triangles"] for m in self.meshes)
        if triangle_count > 50000:
            self.warnings.append("More than 50,000 triangles: review the viewing distance, instances and silhouette budget.")
        if len(self.materials) > 8:
            self.warnings.append("More than 8 materials: review draw calls and atlas/shared material opportunities.")
        if any(max(t["width"], t["height"]) > 2048 for t in self.textures.values()):
            self.warnings.append("Textures exceed 2048 px: review visible texel density before import.")
        if sum(m["submeshes"] for m in self.meshes) > 16:
            self.warnings.append("More than 16 submeshes/draw sections: consider combining static meshes that share materials.")
        document = {"schemaVersion": "1", "ownership": {"key": key("blender_", self.args.name)},
                    "slot": {"key": "root", "name": self.args.name, "parent": self.args.parent},
                    "assets": self.assets, "components": root_components, "children": children}
        # Manifest last: a failed extraction has no complete apply entrypoint.
        write_json(self.output / "model.apply.json", document)
        report = {"source": bpy.data.filepath, "blenderVersion": bpy.app.version_string,
                  "applyFile": str(self.output / "model.apply.json"), "outputDirectory": str(self.output),
                  "coordinateMapping": ("Blender local mesh and parent TRS -> Resonite (x,z,y) metres; UV unchanged" if getattr(self.args, "preserve_hierarchy", False)
                                        else "Blender world (x,y,z) * scale_length -> Resonite (x,z,y) metres; UV unchanged"),
                  "staticOnly": True, "rendered": False, "triangles": triangle_count,
                  "preserveHierarchy": getattr(self.args, "preserve_hierarchy", False),
                  "providerLayout": "root" if getattr(self.args, "legacy_root_providers", False) else "slots",
                  "vertices": sum(m["vertices"] for m in self.meshes), "materials": len(self.materials),
                  "drawSections": sum(m["submeshes"] for m in self.meshes), "meshes": self.meshes,
                  "textures": list(self.textures.values()), "warnings": self.warnings}
        write_json(self.output / "report.json", report)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--parent", required=True)
    parser.add_argument("--collection")
    parser.add_argument("--preserve-hierarchy", action="store_true")
    parser.add_argument("--pack-pbr", action="store_true")
    parser.add_argument("--legacy-root-providers", action="store_true")
    Exporter(parser.parse_args(sys.argv[sys.argv.index("--") + 1:])).run()
