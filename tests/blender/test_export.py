"""Opt-in real Blender tests without a Resonite connection.

resoloop blender run tests/blender/test_export.py --arg=src/RLoop.ResoniteLink/blender_export.py --json
"""
import argparse
import importlib.util
import json
import math
import subprocess
from pathlib import Path
import sys
import tempfile
import unittest
import bpy
from mathutils import Vector, Matrix, Quaternion

sys.dont_write_bytecode = True

source = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
spec = importlib.util.spec_from_file_location("resoloop_export", source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def all_components(document):
    return document.get("components", []) + [c for child in document.get("children", []) for c in all_components(child)]


class ExportTests(unittest.TestCase):
    def test_saved_source_exports_identically_in_fresh_processes_and_keeps_uv_seams(self):
        blend = Path(self.temp.name) / "repeat.blend"
        bpy.ops.wm.save_as_mainfile(filepath=str(blend))
        outputs = []
        for name in ("fresh-a", "fresh-b"):
            directory = Path(self.temp.name) / name
            subprocess.run([bpy.app.binary_path, "--background", "--factory-startup", "--disable-autoexec",
                            str(blend), "--python-exit-code", "1", "--python", str(source), "--",
                            "--output", str(directory), "--name", "Prop", "--parent", "Root"],
                           check=True, capture_output=True, timeout=90)
            outputs.append({p.name: p.read_bytes() for p in directory.iterdir() if p.name != "report.json"})
        self.assertEqual(outputs[0], outputs[1])
        mesh = json.loads(next(data for name, data in outputs[0].items() if name.endswith(".mesh.json")))
        self.assertEqual(5, len(mesh["vertices"]))

    def setUp(self):
        bpy.ops.object.select_all(action="SELECT")
        bpy.ops.object.delete(use_global=False)
        self.temp = tempfile.TemporaryDirectory(prefix="resoloop-blender-test-")
        self.addCleanup(self.temp.cleanup)
        mesh = bpy.data.meshes.new("Corners")
        mesh.from_pydata([(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)], [], [(0, 1, 2), (0, 2, 3)])
        self.obj = bpy.data.objects.new("TestMesh", mesh)
        bpy.context.collection.objects.link(self.obj)
        for name in ("BaseUV", "LightUV"):
            layer = mesh.uv_layers.new(name=name)
            for loop, uv in zip(layer.data, [(0, 0), (1, 0), (1, 1), (0.25, 0), (1, 1), (0, 1)]):
                loop.uv = uv
        mesh.uv_layers["BaseUV"].active_render = True
        self.mat = bpy.data.materials.new("A")
        self.mat.use_nodes = True
        second = bpy.data.materials.new("B")
        second.use_nodes = True
        mesh.materials.append(self.mat)
        mesh.materials.append(second)
        mesh.polygons[1].material_index = 1
        self.obj.location = (1, 2, 3)
        self.obj.scale = (2, 3, 4)
        bpy.context.scene.unit_settings.scale_length = 0.1

    def export(self, name="bundle", collection=None, **options):
        path = Path(self.temp.name) / name
        args = argparse.Namespace(output=str(path), name="Prop", parent="Root/Verified_Test", collection=collection)
        for key, value in options.items():
            setattr(args, key, value)
        module.Exporter(args).run()
        return path

    def test_uv_seams_normals_handedness_units_and_material_order(self):
        colors = self.obj.data.color_attributes.new(name="Color", type="FLOAT_COLOR", domain="CORNER")
        self.obj.data.color_attributes.active_color = colors
        for item in colors.data:
            item.color = (0.2, 0.3, 0.4, 1)
        for mirrored in (False, True):
            with self.subTest(mirrored=mirrored):
                self.obj.scale.x = -2 if mirrored else 2
                path = self.export(str(mirrored))
                mesh = json.loads(next(path.glob("*.mesh.json")).read_text())
                self.assertEqual(5, len(mesh["vertices"]))  # one UV seam split, not one vertex per corner
                self.assertEqual(2, len(mesh["submeshes"]))
                first = mesh["vertices"][0]
                for actual, expected in zip(first["position"].values(), (0.1, 0.3, 0.2)):
                    self.assertAlmostEqual(actual, expected, places=5)
                self.assertEqual(2, len(first["uvs"]))
                self.assertAlmostEqual(0.2, first["color"]["r"], places=5)
                self.assertEqual(1 if mirrored else -1, first["tangent"]["w"])
                for submesh in mesh["submeshes"]:
                    vertices = [mesh["vertices"][i] for i in submesh["vertexIndices"]]
                    a, b, c = [Vector(tuple(v["position"].values())) for v in vertices]
                    normal = Vector(tuple(vertices[0]["normal"].values()))
                    self.assertGreater((b-a).cross(c-a).normalized().dot(normal), 0.999)
                document = json.loads((path / "model.apply.json").read_text())
                renderer = document["children"][0]["components"][1]
                self.assertEqual(2, len(renderer["fields"]["Materials"]))

    def test_normal_texture_and_color_are_saved_and_bound(self):
        image = bpy.data.images.new("Normal", width=8, height=8)
        image.colorspace_settings.name = "Non-Color"
        image.pixels[:] = [0.5, 0.5, 1, 1] * 64
        image.pack()
        nodes = self.mat.node_tree.nodes
        texture = nodes.new("ShaderNodeTexImage")
        texture.image = image
        normal = nodes.new("ShaderNodeNormalMap")
        self.mat.node_tree.links.new(texture.outputs["Color"], normal.inputs["Color"])
        self.mat.node_tree.links.new(normal.outputs["Normal"], nodes.get("Principled BSDF").inputs["Normal"])
        path = self.export()
        document = json.loads((path / "model.apply.json").read_text())
        provider = next(c for c in all_components(document) if c["type"].endswith("StaticTexture2D"))
        self.assertTrue(provider["fields"]["IsNormalMap"])
        self.assertTrue(provider["fields"]["MipMaps"])
        material = next(c for c in all_components(document) if "NormalMap" in c["fields"])
        self.assertEqual("$component:" + provider["key"], material["fields"]["NormalMap"])
        png = bpy.data.images.load(str(next(path.glob("*.png"))))
        try:
            png.colorspace_settings.name = "Non-Color"
            self.assertAlmostEqual(0.5, png.pixels[0], delta=0.01)
            self.assertAlmostEqual(1.0, png.pixels[2], delta=0.01)
        finally:
            bpy.data.images.remove(png)

    def test_unsupported_shader_fails_without_manifest(self):
        self.mat.node_tree.nodes.get("Principled BSDF").inputs["Coat Weight"].default_value = 0.5
        with self.assertRaisesRegex(ValueError, "unsupported"):
            self.export()
        self.assertFalse((Path(self.temp.name) / "bundle" / "model.apply.json").exists())

    def test_linear_socket_color_is_encoded_for_runtime_srgb(self):
        shader = self.mat.node_tree.nodes.get("Principled BSDF")
        shader.inputs["Base Color"].default_value = (0.18, 0.18, 0.18, 1)
        path = self.export()
        document = json.loads((path / "model.apply.json").read_text())
        material = next(c for c in all_components(document) if c["type"].endswith("PBS_Metallic"))
        self.assertAlmostEqual(0.461356, material["fields"]["AlbedoColor"][0], places=5)
        self.assertEqual(1, material["fields"]["AlbedoColor"][3])

    def test_ngon_triangulation_keeps_corner_data_and_source(self):
        mesh = bpy.data.meshes.new("Ngon")
        vertices = [(math.cos(i*math.tau/5), math.sin(i*math.tau/5), 0) for i in range(5)]
        mesh.from_pydata(vertices, [], [(0, 1, 2, 3, 4)])
        self.obj.data = mesh
        mesh.materials.append(self.mat)
        mesh.polygons[0].use_smooth = True
        for name in ("UV0", "UV1"):
            uv = mesh.uv_layers.new(name=name)
            for i, corner in enumerate(uv.data):
                corner.uv = ((vertices[i][0]+1)/2, (vertices[i][1]+1)/2)
        mesh.uv_layers["UV0"].active_render = True
        colors = mesh.color_attributes.new(name="Color", type="FLOAT_COLOR", domain="CORNER")
        mesh.color_attributes.active_color = colors
        for i, item in enumerate(colors.data):
            item.color = (i/5, 0, 0, 1)
        custom = Vector((0.1, 0.2, 1)).normalized()
        mesh.normals_split_custom_set([custom] * 5)
        path = self.export()
        result = json.loads(next(path.glob("*.mesh.json")).read_text())
        self.assertEqual(9, len(result["submeshes"][0]["vertexIndices"]))
        self.assertEqual(5, mesh.polygons[0].loop_total)
        self.assertEqual(1, len(mesh.polygons))
        expected_normal = Vector((custom.x/2, custom.z/4, custom.y/3)).normalized()
        for vertex in result["vertices"]:
            self.assertEqual(2, len(vertex["uvs"]))
            self.assertGreater(Vector(tuple(vertex["normal"].values())).dot(expected_normal), 0.999)
            self.assertTrue(all(math.isfinite(v) for v in vertex["tangent"].values()))
            index = round(vertex["color"]["r"]*5)
            self.assertAlmostEqual((vertices[index][0]+1)/2, vertex["uvs"][0]["uv"]["x"], places=5)

    def test_unpacked_generated_pixels_are_saved_without_copy_loss(self):
        image = bpy.data.images.new("UnsavedPixels", width=8, height=8, alpha=True)
        image.pixels[:] = [1, 0, 0, 1] * 64
        node = self.mat.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        self.mat.node_tree.links.new(node.outputs["Color"], self.mat.node_tree.nodes.get("Principled BSDF").inputs["Base Color"])
        old_path, old_format = image.filepath, image.file_format
        path = self.export()
        png = bpy.data.images.load(str(next(path.glob("*.png"))))
        try:
            self.assertGreater(png.pixels[0], 0.99)
            self.assertLess(png.pixels[1], 0.01)
            self.assertEqual(old_path, image.filepath)
            self.assertEqual(old_format, image.file_format)
            self.assertIsNone(image.packed_file)
        finally:
            bpy.data.images.remove(png)

    def test_saved_generated_image_survives_blend_reopen(self):
        image = bpy.data.images.new("SavedPixels", width=8, height=8, alpha=True)
        image.pixels[:] = [0, 0, 1, 1] * 64
        image.file_format = "PNG"
        image.filepath_raw = str(Path(self.temp.name) / "saved.png")
        image.save()
        node = self.mat.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        self.mat.node_tree.links.new(node.outputs["Color"], self.mat.node_tree.nodes.get("Principled BSDF").inputs["Base Color"])
        blend = str(Path(self.temp.name) / "saved.blend")
        bpy.ops.wm.save_as_mainfile(filepath=blend)
        bpy.ops.wm.open_mainfile(filepath=blend)
        path = self.export()
        png = bpy.data.images.load(str(next(path.glob("*.png"))))
        try:
            self.assertGreater(png.pixels[2], 0.99)
            self.assertLess(png.pixels[0], 0.01)
        finally:
            bpy.data.images.remove(png)

    def test_dirty_file_image_uses_current_pixels_not_older_file(self):
        image = bpy.data.images.new("DirtyPixels", width=8, height=8, alpha=True)
        original = str(Path(self.temp.name) / "original.png")
        image.pixels[:] = [1, 0, 0, 1] * 64
        image.file_format = "PNG"
        image.save(filepath=original)
        loaded = bpy.data.images.load(original, check_existing=False)
        loaded.pixels[:] = [0, 1, 0, 1] * 64
        node = self.mat.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = loaded
        self.mat.node_tree.links.new(node.outputs["Color"], self.mat.node_tree.nodes.get("Principled BSDF").inputs["Base Color"])
        path = self.export()
        exported = bpy.data.images.load(str(next(path.glob("*.png"))))
        original_image = bpy.data.images.load(original, check_existing=False)
        try:
            self.assertGreater(exported.pixels[1], 0.99)
            self.assertLess(exported.pixels[0], 0.01)
            self.assertGreater(original_image.pixels[0], 0.99)
        finally:
            bpy.data.images.remove(exported)
            bpy.data.images.remove(original_image)

    def test_missing_texture_identifies_image_path_and_recovery(self):
        image = bpy.data.images.new("MissingTexture", width=8, height=8)
        image.source = "FILE"
        image.filepath = str(Path(self.temp.name) / "missing.png")
        image.buffers_free()
        node = self.mat.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        self.mat.node_tree.links.new(node.outputs["Color"], self.mat.node_tree.nodes.get("Principled BSDF").inputs["Base Color"])
        with self.assertRaisesRegex(ValueError, "MissingTexture.*missing.png.*reload"):
            self.export()
        self.assertFalse((Path(self.temp.name) / "bundle" / "model.apply.json").exists())

    def test_missing_collection_and_existing_output_fail(self):
        with self.assertRaisesRegex(ValueError, "Collection not found"):
            self.export(collection="DoesNotExist")
        self.export()
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.export()

    def test_hidden_geometry_not_exported(self):
        self.obj.hide_render = True
        with self.assertRaisesRegex(ValueError, "No visible mesh"):
            self.export()

    def test_provider_slots_are_unique_and_legacy_layout_is_explicit(self):
        path = self.export()
        document = json.loads((path / "model.apply.json").read_text())
        providers = next(n for n in document["children"] if n["slot"]["key"] == "blender_providers")["children"]
        self.assertEqual(2, len(providers))
        self.assertEqual(2, len({p["slot"]["key"] for p in providers}))
        self.assertTrue(all(len(p["components"]) == 1 for p in providers))
        legacy = json.loads((self.export("legacy", legacy_root_providers=True) / "model.apply.json").read_text())
        self.assertEqual(2, len(legacy["components"]))

    def test_hierarchy_preserves_pivot_world_geometry_and_mirror_normals(self):
        parent = bpy.data.objects.new("Pivot", None)
        bpy.context.collection.objects.link(parent)
        parent.location = (4, 2, 1)
        parent.rotation_euler = (0.2, 0.1, 0.3)
        parent.scale = (1.4, 1.2, 0.9)
        self.obj.parent = parent
        basis = Matrix(((1,0,0,0),(0,0,1,0),(0,1,0,0),(0,0,0,1)))
        for mirrored in (False, True):
            parent.scale.x = -1.4 if mirrored else 1.4
            bpy.context.view_layer.update()
            path = self.export(str(mirrored), preserve_hierarchy=True)
            document = json.loads((path / "model.apply.json").read_text())
            pivot = next(n for n in document["children"] if n["slot"]["name"] == "Pivot")
            child = pivot["children"][0]
            def matrix(node):
                s = node["slot"]
                x,y,z,w = s["rotation"]
                return Matrix.LocRotScale(Vector(s["position"]), Quaternion((w,x,y,z)), Vector(s["scale"]))
            world = matrix(pivot) @ matrix(child)
            expected_pivot = (basis @ self.obj.matrix_world).translation * 0.1
            self.assertLess((world.translation-expected_pivot).length, 1e-5)
            mesh = json.loads(next(path.glob("*.mesh.json")).read_text())
            for section in mesh["submeshes"]:
                for i in range(0,len(section["vertexIndices"]),3):
                    vs = [mesh["vertices"][j] for j in section["vertexIndices"][i:i+3]]
                    a,b,c = [world @ Vector(tuple(v["position"].values())) for v in vs]
                    normal = (world.to_3x3().inverted().transposed() @ Vector(tuple(vs[0]["normal"].values()))).normalized()
                    self.assertGreater((b-a).cross(c-a).normalized().dot(normal), 0.999)

    def test_packing_preserves_midrange_linear_data_and_sets_profile(self):
        for name, amount in (("Metallic", 0.25), ("Roughness", 0.7)):
            image = bpy.data.images.new(name, width=4, height=4, alpha=True)
            image.colorspace_settings.name = "Non-Color"
            image.pixels[:] = [amount, amount, amount, 1] * 16
            node = self.mat.node_tree.nodes.new("ShaderNodeTexImage")
            node.image = image
            self.mat.node_tree.links.new(node.outputs["Color"], self.mat.node_tree.nodes.get("Principled BSDF").inputs[name])
        with self.assertRaisesRegex(ValueError, "unsupported linked input"):
            self.export("unapproved")
        path = self.export("packed", pack_pbr=True)
        document = json.loads((path / "model.apply.json").read_text())
        provider = next(c for c in all_components(document) if c["fields"].get("PreferredProfile") == "Linear")
        asset = document["assets"][provider["fields"]["URL"].removeprefix("$asset:")]
        image = bpy.data.images.load(str(path / asset["source"]))
        try:
            image.colorspace_settings.name = "Non-Color"
            self.assertAlmostEqual(0.25, image.pixels[0], delta=0.005)
            self.assertAlmostEqual(0.3, image.pixels[3], delta=0.005)
        finally:
            bpy.data.images.remove(image)
        roughness_node = self.mat.node_tree.nodes.get("Principled BSDF").inputs["Roughness"].links[0].from_node
        roughness_node.image.scale(8, 8)
        with self.assertRaisesRegex(ValueError, "equal image dimensions"):
            self.export("mismatch", pack_pbr=True)
        self.assertFalse((Path(self.temp.name) / "mismatch" / "model.apply.json").exists())


result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(ExportTests))
if not result.wasSuccessful():
    raise RuntimeError("Blender exporter regression tests failed")
