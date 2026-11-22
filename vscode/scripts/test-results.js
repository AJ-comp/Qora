const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

// Exercise the actual extension helpers without starting a VS Code extension host.
const source = fs.readFileSync(path.join(__dirname, '..', 'extension.js'), 'utf8');
const sandbox = {
  module: { exports: {} },
  require: (name) => name === 'vscode' ? { env: { language: 'en' } } : require(name),
};
vm.runInNewContext(source + '\nmodule.exports = { DOCS, qasmSummary, parseRunnerReply, validateEntryResultMetadata, validateRunResult, appendEntryResult, runL10n };', sandbox);
const { DOCS, qasmSummary, parseRunnerReply, validateEntryResultMetadata, validateRunResult, appendEntryResult, runL10n } = sandbox.module.exports;

function render(reply) {
  const lines = [];
  appendEntryResult({ appendLine: (line) => lines.push(line) }, reply, runL10n());
  return lines.join('\n');
}

test('bool hover documents logical values separately from measured bits', () => {
  assert.equal(typeof DOCS.bool, 'string');
  assert.match(DOCS.bool, /true.*false/);
  assert.match(DOCS.bool, /bit/);
});

test('mutable-array hover documentation includes Boolean arrays', () => {
  assert.match(DOCS.var, /bool\[\]/);
  assert.match(DOCS.move, /bool\[\]/);
  assert.match(DOCS.var, /var bit\[\].*QASM002/);
  assert.match(DOCS.move, /bit\[\]/);
  assert.match(DOCS.new, /bool\[\].*false/);
});

test('function and array snippet type choices include bool', () => {
  const snippets = JSON.parse(fs.readFileSync(
    path.join(__dirname, '..', 'snippets', 'qora.code-snippets'), 'utf8'));
  const returnTypes = snippets.function.body[0].match(/\$\{3\|([^|]+)\|\}/)[1].split(',');
  const elementTypes = snippets['classical array'].body[0].match(/\$\{1\|([^|]+)\|\}/)[1].split(',');
  for (const type of ['bool', 'bit', 'int', 'float', 'angle']) {
    assert.ok(returnTypes.includes(type), `missing function return type: ${type}`);
    assert.ok(elementTypes.includes(type), `missing array element type: ${type}`);
  }
  assert.match(snippets['classical array'].description, /bool.*false/);
  assert.match(snippets['operation returning bits'].body[0], /:\s*bit\[\]\s*\{/);
  assert.ok(!returnTypes.includes('bit[]'), 'array returns belong to operations, not pure functions');
});

test('program summary does not count Boolean or array declarations as gates', () => {
  const summary = qasmSummary(`OPENQASM 3.0;
include "stdgates.inc";
output bool returned;
bool ready;
array[bool, 2] flags;
array[int[32], 2] values;
qubit[2] q;
x q[0];
returned = measure q[0];
`);
  assert.equal(summary.qubits, 2);
  assert.equal(summary.gates, 1);
  assert.equal(summary.measures, 1);
});

test('old compiler metadata is rejected explicitly', () => {
  assert.throws(() => validateEntryResultMetadata(undefined), /Incompatible Qora compiler/);
  assert.throws(() => validateEntryResultMetadata(null), /Incompatible Qora compiler/);
});

test('runner rejects null, arrays, partial JSON, and failed processes', () => {
  for (const stdout of ['null', '[]', '1', '"text"', '', 'notice\n{"shots":1}']) {
    assert.throws(() => parseRunnerReply({ code: 0, stdout, stderr: '' }));
  }
  assert.throws(() => parseRunnerReply({ code: 1, stdout: '{"shots":1}', stderr: '' }), /exited with code 1/);
  assert.throws(() => parseRunnerReply({ code: 1, stdout: '{"error":"bad output"}', stderr: '' }), /bad output/);
  assert.equal(1, parseRunnerReply({ code: 0, stdout: '{"shots":1}', stderr: '' }).shots);
});

test('return metadata specifies an output name and a register width for bit[]', () => {
  for (const type of ['void', 'bool', 'bit', 'int', 'float', 'angle']) {
    validateEntryResultMetadata({ type, outputName: 'renamed_result_7' });
  }
  validateEntryResultMetadata({ type: 'bit[]', length: 1, outputName: 'single_bit_register' });
  for (const value of [{ type: 'bit[]', outputName: 'x' }, { type: 'bit[]', length: 0, outputName: 'x' }, { type: 'int' }, { type: 'int', outputName: '' }]) {
    assert.throws(() => validateEntryResultMetadata(value), /Invalid entryResult/);
  }
});

test('void reports completion and shots without histogram or values', () => {
  const reply = { shots: 2, result: { type: 'void' } };
  validateRunResult(reply, { type: 'void' }, 2);
  const output = render(reply);
  assert.match(output, /2 shots/);
  assert.match(output, /void; no result values/);
  assert.doesNotMatch(output, /█/);
  assert.throws(() => validateRunResult({ shots: 2, result: { type: 'void', values: [] } }, { type: 'void' }, 2), /void/);
});

test('large signed integer values stay exact decimal strings', () => {
  const reply = { shots: 2, result: { type: 'int', values: ['9007199254740993', '-9223372036854775808'] } };
  validateRunResult(reply, { type: 'int' }, 2);
  const output = render(reply);
  assert.match(output, /9007199254740993/);
  assert.match(output, /-9223372036854775808/);
  assert.match(output, /Entry return values: int/);
});

test('bool, bit, float and angle render as their declared return types', () => {
  for (const [type, value, label] of [['bool', true, 'true'], ['bool', false, 'false'], ['bit', 1, '1'], ['float', 1.25, '1.25'], ['angle', Math.PI / 2, '1.5707963267948966']]) {
    const reply = { shots: 2, result: { type, values: [value, value] } };
    validateRunResult(reply, { type }, 2);
    const output = render(reply);
    assert.ok(output.includes(label));
    assert.ok(output.includes(`Entry return values: ${type}`));
    assert.match(output, /100.0%/);
    if (type === 'angle') assert.match(output, /radians/);
  }
});

test('invalid result types, counts and scalar values are rejected', () => {
  assert.throws(() => validateRunResult({ shots: 1, result: { type: 'bit', values: [0] } }, { type: 'int' }, 1), /type or shot count/);
  assert.throws(() => validateRunResult({ shots: 2, result: { type: 'bit', values: [0] } }, { type: 'bit' }, 2), /one entry value/);
  for (const [type, value] of [['bool', 0], ['bool', 1], ['bool', 'true'], ['bit', true], ['bit', 2], ['int', true], ['int', 9007199254740992], ['int', '1.5'], ['float', Infinity], ['angle', NaN]]) {
    assert.throws(() => validateRunResult({ shots: 1, result: { type, values: [value] } }, { type }, 1), /Invalid/);
  }
});

test('bit[] results preserve register width, element order and shot boundaries', () => {
  const metadata = { type: 'bit[]', length: 2 };
  const reply = { shots: 3, result: { type: 'bit[]', values: [[1, 0], [0, 1], [1, 0]] } };
  validateRunResult(reply, metadata, 3);
  assert.match(render(reply), /\[1, 0\].*2\s+\(66\.7%\)/);
  assert.match(render(reply), /\[0, 1\].*1\s+\(33\.3%\)/);
  for (const value of [[0], [1, 2], [true, false], 1]) {
    assert.throws(() => validateRunResult({ shots: 1, result: { type: 'bit[]', values: [value] } }, metadata, 1), /Invalid/);
  }
});

test('floating point signed zero remains visible', () => {
  const reply = { shots: 2, result: { type: 'float', values: [-0, 0] } };
  const output = render(reply);
  assert.match(output, /     -0  /);
  assert.match(output, /     0  /);
});
