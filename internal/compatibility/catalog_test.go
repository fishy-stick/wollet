package compatibility

import (
	"encoding/json"
	"os"
	"testing"
)

func TestSharedFixtures(t *testing.T) {
	data, err := os.ReadFile("fixtures.json")
	if err != nil {
		t.Fatal(err)
	}
	var cases []struct {
		Name                    string
		Client, Server          Endpoint
		Kind, Component, Target string
		Count                   int
	}
	if err = json.Unmarshal(data, &cases); err != nil {
		t.Fatal(err)
	}
	for _, test := range cases {
		t.Run(test.Name, func(t *testing.T) {
			got := Default.Evaluate(test.Client, test.Server)
			if got.Kind != test.Kind || len(got.Missing) != test.Count {
				t.Fatalf("got %+v", got)
			}
			for _, m := range got.Missing {
				if m.Component != test.Component || m.Target != test.Target {
					t.Fatalf("missing %+v", m)
				}
			}
		})
	}
}
func TestCatalogIntegrity(t *testing.T) {
	if _, ok := Default.Profiles[Default.CurrentProfile]; !ok {
		t.Fatal("missing current profile")
	}
	seen := map[string]bool{}
	orders := map[int]bool{}
	for _, r := range Default.Versions {
		n := Normalize(r.Version)
		if n == "" || seen[n] || orders[r.Order] {
			t.Fatalf("invalid release %+v", r)
		}
		seen[n] = true
		orders[r.Order] = true
		if _, ok := Default.Profiles[r.Profile]; !ok {
			t.Fatal(r.Profile)
		}
	}
	seen = map[string]bool{}
	for _, f := range Default.Features {
		if f.ID == "" || f.Name == "" || seen[f.ID] {
			t.Fatal(f.ID)
		}
		seen[f.ID] = true
	}
}
func TestNormalize(t *testing.T) {
	for input, want := range map[string]string{"1.0": "1.0.0", "1.0.0.0": "1.0.0", "1.0.0.1": "1.0.0.1", "1.1.0-RC.1+abc": "1.1.0-rc.1", "v1.0.0": "", "1.0.0\n": "", "999999999999.0.0": ""} {
		if got := Normalize(input); got != want {
			t.Errorf("%q: %q != %q", input, got, want)
		}
	}
}
