"""Validate and decode the compiler-declared entry-point result, never internal measurements."""

import math


def validate_entry_result(entry_result):
    """Require the metadata supplied by the same compiler that emitted the QASM."""
    if not isinstance(entry_result, dict):
        raise ValueError(
            "Incompatible Qora compiler: entryResult metadata is missing. "
            "Update the extension's compiler bundle or rebuild the configured Qora CLI."
        )
    if entry_result.get("type") not in ("void", "bool", "bit", "int", "float", "angle"):
        raise ValueError("Unsupported entryResult type in Qora compiler output")
    name = entry_result.get("outputName")
    if not isinstance(name, str) or not name.strip():
        raise ValueError("Qora compiler entryResult metadata has no outputName")
    return entry_result


def validate_execution_request(request):
    if not isinstance(request, dict):
        raise ValueError("Expected a JSON object containing qasm and entryResult")
    validate_entry_result(request.get("entryResult"))
    if not isinstance(request.get("qasm"), str) or not request["qasm"].strip():
        raise ValueError("No compiled QASM in execution request")
    return request


def validate_shots(shots):
    if type(shots) is not int or shots <= 0:
        raise ValueError("shots must be a positive integer")


def _return_value(value, return_type, shot_index):
    where = f"Entry return value in shot {shot_index}"
    if return_type == "bool":
        if type(value) is not bool:
            raise ValueError(f"{where} is not a bool (true or false)")
        return value
    if return_type == "bit":
        if type(value) is not int or value not in (0, 1):
            raise ValueError(f"{where} is not a bit (0 or 1)")
        return value
    if return_type == "int":
        if type(value) is not int:
            raise ValueError(f"{where} is not an integer")
        # JavaScript JSON numbers cannot exactly represent every Qora integer.
        return str(value)
    if type(value) not in (int, float):
        raise ValueError(f"{where} is not a finite {return_type}")
    try:
        number = float(value)
    except OverflowError as error:
        raise ValueError(f"{where} is not a finite {return_type}") from error
    if not math.isfinite(number):
        raise ValueError(f"{where} is not a finite {return_type}")
    return number


def entry_result_from_task(task_result, entry_result, shots):
    """Preserve shot order and expose only the entry point's declared scalar return value."""
    validate_entry_result(entry_result)
    validate_shots(shots)
    reported_shots = getattr(getattr(task_result, "task_metadata", None), "shots", None)
    if type(reported_shots) is not int or reported_shots != shots:
        raise ValueError(f"Braket task shot count {reported_shots!r} does not match requested shots {shots}")
    outputs = getattr(task_result, "outputs", None)
    if not isinstance(outputs, list):
        raise ValueError(
            "Braket did not return declared outputs. Update the compiler and the pinned "
            "Braket runtime; internal measurement counts are not entry return values."
        )
    if len(outputs) != shots:
        raise ValueError(
            f"Braket output shot count {len(outputs)} does not match requested shots {shots}; "
            "incomplete samples cannot be reported as successful execution"
        )

    name = entry_result["outputName"]
    return_type = entry_result["type"]
    values = []
    for index, shot in enumerate(outputs):
        if not isinstance(shot, dict) or set(shot) != {name}:
            raise ValueError(f"Braket output names in shot {index} do not match entryResult metadata")
        value = shot[name]
        if return_type == "void":
            if type(value) is not int or value != 1:
                raise ValueError(f"Missing entry completion marker in shot {index}")
        else:
            values.append(_return_value(value, return_type, index))

    result = {"type": return_type}
    if return_type != "void":
        result["values"] = values
    return {"shots": shots, "result": result}
