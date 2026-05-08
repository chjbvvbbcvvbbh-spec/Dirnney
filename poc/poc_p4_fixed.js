"use strict";
let pass=0, fail=0;

function check(l,a,b){
  const ok = JSON.stringify(a)===JSON.stringify(b);
  print((ok?"[PASS] ":"[FAIL] ")+l);
  if(!ok){ print("       got="+JSON.stringify(a)+" want="+JSON.stringify(b)); }
  ok ? pass++ : fail++;
}

function diff(label, fn, arg) {
  function interpWrapper(a) { return fn(a); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(arg);

  function optWrapper(a) { return fn(a); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(i & 0x7f);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(arg);
  check(label, iv, ov);
}

// P4.1 and P4.2 were wrong: (0).x returns undefined in both tiers (spec correct)
diff("P4.1 fixed: (0).y same in interp and JIT", function(x){
  try { return (x).y; } catch(e) { return "TypeError"; }
}, 0);

diff("P4.2 fixed: (1).y same in interp and JIT", function(x){
  try { return (x).y; } catch(e) { return "TypeError"; }
}, 1);

// True Smi confusion guard test: null/undefined must throw in both tiers
diff("P4.3: null.y → TypeError in both tiers", function(x){
  try { return x.y; } catch(e) { return "TypeError"; }
}, null);

diff("P4.4: undefined.y → TypeError in both tiers", function(x){
  try { return x.y; } catch(e) { return "TypeError"; }
}, undefined);

print("P4 final: " + pass + " pass, " + fail + " fail");
